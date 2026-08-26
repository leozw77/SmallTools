using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeTypeAudioGuard;

internal sealed class AudioGuardService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly AppLogger _log;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, SavedSession> _idleBaseline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SavedSession> _original = new(StringComparer.Ordinal);
    private static Guid ChangeContext = new("E4056C42-CDE2-4945-B786-38D5626F8F01");
    private volatile bool _isCapturing;

    public bool IsCapturing => _isCapturing;
    public event Action<bool>? CaptureStateChanged;

    public AudioGuardService(Func<AppSettings> settings, AppLogger log)
    {
        _settings = settings;
        _log = log;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WeTypeAudioGuard.CoreAudio"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _log.Write("[START] audio guard service started");

            while (!_cts.IsCancellationRequested)
            {
                var settings = _settings();
                bool captureNow = settings.Enabled && IsWeTypeCaptureActive(enumerator);

                if (captureNow && !_isCapturing)
                {
                    _isCapturing = true;
                    BeginCapture(enumerator, settings);
                    RaiseStateChanged(true);
                }
                else if (!captureNow && _isCapturing)
                {
                    RestoreOriginal(enumerator, "voice-end");
                    _isCapturing = false;
                    _log.Write("[VOICE END] original audio state restored");
                    RaiseStateChanged(false);

                    // WeType can perform delayed cleanup after capture stops. Re-apply once
                    // after that window so its cleanup cannot leave a session muted.
                    if (!_cts.Token.WaitHandle.WaitOne(1000))
                    {
                        if (!IsWeTypeCaptureActive(enumerator))
                            RestoreOriginal(enumerator, "post-cleanup");
                    }

                    RefreshIdleBaseline(enumerator);
                }

                if (_isCapturing)
                {
                    EnforcePolicy(enumerator, settings);
                }
                else
                {
                    // Keep a pre-voice snapshot while idle. In the diagnostic trace, WeType
                    // sets render sessions to Mute at essentially the same instant its capture
                    // session becomes Active, so snapshotting only after detection is too late.
                    RefreshIdleBaseline(enumerator);
                }

                int delay = _isCapturing ? 40 : 100;
                if (_cts.Token.WaitHandle.WaitOne(delay)) break;
            }
        }
        catch (Exception ex)
        {
            _log.Write("[FATAL] " + ex);
        }
        finally
        {
            try
            {
                if (enumerator is not null && _original.Count > 0)
                    RestoreOriginal(enumerator, "service-stop");
            }
            catch { }
            ComUtil.SafeRelease(enumerator);
            _log.Write("[STOP] audio guard service stopped");
        }
    }

    private bool IsWeTypeCaptureActive(IMMDeviceEnumerator enumerator)
    {
        foreach (var role in new[] { ERole.eMultimedia, ERole.eConsole, ERole.eCommunications })
        {
            IMMDevice? device = null;
            object? managerObj = null;
            IAudioSessionEnumerator? sessions = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, role, out device) < 0 || device is null)
                    continue;

                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out managerObj) < 0 || managerObj is null)
                    continue;

                var manager = (IAudioSessionManager2)managerObj;
                if (manager.GetSessionEnumerator(out sessions) < 0 || sessions is null)
                    continue;

                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        if (sessions.GetSession(i, out control) < 0 || control is null) continue;
                        var ctl2 = control as IAudioSessionControl2;
                        if (ctl2 is null) continue;
                        control.GetState(out var state);
                        if (state != AudioSessionState.Active) continue;
                        ctl2.GetProcessId(out uint pid);
                        var name = GetProcessName(pid);
                        if (IsWeTypeProcess(name)) return true;
                    }
                    finally
                    {
                        ComUtil.SafeRelease(control);
                    }
                }
            }
            catch { }
            finally
            {
                ComUtil.SafeRelease(sessions);
                ComUtil.SafeRelease(managerObj);
                ComUtil.SafeRelease(device);
            }
        }
        return false;
    }

    private void BeginCapture(IMMDeviceEnumerator enumerator, AppSettings settings)
    {
        _original.Clear();

        // Copy the most recent known-good state captured before WeType became active.
        foreach (var pair in _idleBaseline)
            _original[pair.Key] = pair.Value;

        // Merge any sessions that appeared after the last idle baseline sample. If a
        // currently-active new session is already muted, WeType most likely muted it.
        foreach (var live in EnumerateRenderSessions(enumerator))
        {
            try
            {
                if (live.ProcessId == (uint)Environment.ProcessId || IsWeTypeProcess(live.ProcessName))
                    continue;

                if (!_original.ContainsKey(live.Key))
                {
                    bool assumeOriginallyMuted = live.Mute && live.State != AudioSessionState.Active;
                    _original[live.Key] = new SavedSession(
                        live.Key, live.ProcessId, live.ProcessName, live.Volume, assumeOriginallyMuted);
                }
            }
            finally
            {
                live.Dispose();
            }
        }

        _log.Write($"[VOICE START] baseline={_idleBaseline.Count}, protected={_original.Count}, target={settings.VoicePercent}%");

        // Do not wait for the next loop before undoing WeType's mute.
        EnforcePolicy(enumerator, settings);
    }

    private void RefreshIdleBaseline(IMMDeviceEnumerator enumerator)
    {
        var next = new Dictionary<string, SavedSession>(StringComparer.Ordinal);
        foreach (var live in EnumerateRenderSessions(enumerator))
        {
            try
            {
                if (live.ProcessId == (uint)Environment.ProcessId || IsWeTypeProcess(live.ProcessName))
                    continue;
                next[live.Key] = new SavedSession(
                    live.Key, live.ProcessId, live.ProcessName, live.Volume, live.Mute);
            }
            finally
            {
                live.Dispose();
            }
        }

        _idleBaseline.Clear();
        foreach (var pair in next)
            _idleBaseline[pair.Key] = pair.Value;
    }

    private void EnforcePolicy(IMMDeviceEnumerator enumerator, AppSettings settings)
    {
        float factor = Math.Clamp(settings.VoicePercent, 1, 100) / 100f;
        foreach (var live in EnumerateRenderSessions(enumerator))
        {
            try
            {
                if (live.ProcessId == (uint)Environment.ProcessId || IsWeTypeProcess(live.ProcessName))
                    continue;

                if (!_original.TryGetValue(live.Key, out var original))
                {
                    // A session created while voice input is already active cannot be observed pre-WeType.
                    // If it is actively rendering and already muted, assume WeType muted it.
                    bool assumedOriginalMute = live.Mute && live.State != AudioSessionState.Active;
                    original = new SavedSession(live.Key, live.ProcessId, live.ProcessName, live.Volume, assumedOriginalMute);
                    _original[live.Key] = original;
                }

                if (original.Mute) continue;

                bool scaleVolume = ShouldScaleVolume(live.ProcessName, live.ProcessId);
                float target = scaleVolume
                    ? Math.Clamp(original.Volume * factor, 0f, 1f)
                    : original.Volume;
                bool changed = false;
                if (live.Mute)
                {
                    live.VolumeControl.SetMute(false, ref ChangeContext);
                    changed = true;
                }
                if (scaleVolume && Math.Abs(live.Volume - target) > 0.01f)
                {
                    live.VolumeControl.SetMasterVolume(target, ref ChangeContext);
                    changed = true;
                }

                if (changed)
                    _log.Write($"[PROTECT] {live.ProcessName} pid={live.ProcessId} mute={live.Mute}->False vol={live.Volume:0.00}->{target:0.00}");
            }
            catch (Exception ex)
            {
                _log.Write("[WARN] enforce failed: " + ex.Message);
            }
            finally
            {
                live.Dispose();
            }
        }
    }

    private void RestoreOriginal(IMMDeviceEnumerator enumerator, string reason)
    {
        if (_original.Count == 0) return;
        foreach (var live in EnumerateRenderSessions(enumerator))
        {
            try
            {
                if (!_original.TryGetValue(live.Key, out var original)) continue;
                if (Math.Abs(live.Volume - original.Volume) > 0.01f)
                    live.VolumeControl.SetMasterVolume(original.Volume, ref ChangeContext);
                if (live.Mute != original.Mute)
                    live.VolumeControl.SetMute(original.Mute, ref ChangeContext);
            }
            catch (Exception ex)
            {
                _log.Write($"[WARN] restore({reason}) failed: {ex.Message}");
            }
            finally
            {
                live.Dispose();
            }
        }
    }

    private IEnumerable<LiveSession> EnumerateRenderSessions(IMMDeviceEnumerator enumerator)
    {
        var seenDevices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in new[] { ERole.eMultimedia, ERole.eConsole, ERole.eCommunications })
        {
            IMMDevice? device = null;
            object? managerObj = null;
            IAudioSessionEnumerator? sessions = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out device) < 0 || device is null)
                    continue;
                device.GetId(out string deviceId);
                if (!seenDevices.Add(deviceId)) continue;

                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out managerObj) < 0 || managerObj is null)
                    continue;
                var manager = (IAudioSessionManager2)managerObj;
                if (manager.GetSessionEnumerator(out sessions) < 0 || sessions is null)
                    continue;

                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl? control = null;
                    if (sessions.GetSession(i, out control) < 0 || control is null) continue;

                    bool ownershipTransferred = false;
                    try
                    {
                        var ctl2 = control as IAudioSessionControl2;
                        var volume = control as ISimpleAudioVolume;
                        if (ctl2 is null || volume is null) continue;

                        control.GetState(out var state);
                        ctl2.GetProcessId(out uint pid);
                        ctl2.GetSessionIdentifier(out string sessionId);
                        ctl2.GetSessionInstanceIdentifier(out string instanceId);
                        volume.GetMasterVolume(out float level);
                        volume.GetMute(out bool mute);

                        string processName = GetProcessName(pid);
                        string identity = !string.IsNullOrWhiteSpace(instanceId)
                            ? instanceId
                            : (!string.IsNullOrWhiteSpace(sessionId) ? sessionId : $"{pid}:{i}");
                        string key = $"{deviceId}|{identity}";

                        ownershipTransferred = true;
                        yield return new LiveSession(key, pid, processName, state, level, mute, control, volume);
                    }
                    finally
                    {
                        if (!ownershipTransferred) ComUtil.SafeRelease(control);
                    }
                }
            }
            finally
            {
                ComUtil.SafeRelease(sessions);
                ComUtil.SafeRelease(managerObj);
                ComUtil.SafeRelease(device);
            }
        }
    }

    private static bool IsWeTypeProcess(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.StartsWith("wetype_", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldScaleVolume(string processName, uint pid)
    {
        // audiodg is the Windows audio engine/APO host. WeType may mute its session too,
        // so it must be unmuted, but scaling it together with the player would multiply
        // attenuation (e.g. 30% x 30%). System Sounds are treated the same way.
        if (pid == 0) return false;
        return !processName.Equals("audiodg", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProcessName(uint pid)
    {
        if (pid == 0) return "SystemSounds";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return $"pid-{pid}";
        }
    }

    private void RaiseStateChanged(bool active)
    {
        try { CaptureStateChanged?.Invoke(active); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (!_thread.Join(2500)) _log.Write("[WARN] service thread did not stop within timeout");
        _cts.Dispose();
    }

    private sealed record SavedSession(string Key, uint ProcessId, string ProcessName, float Volume, bool Mute);

    private sealed class LiveSession : IDisposable
    {
        private IAudioSessionControl? _control;
        public string Key { get; }
        public uint ProcessId { get; }
        public string ProcessName { get; }
        public AudioSessionState State { get; }
        public float Volume { get; }
        public bool Mute { get; }
        public ISimpleAudioVolume VolumeControl { get; }

        public LiveSession(string key, uint processId, string processName, AudioSessionState state,
            float volume, bool mute, IAudioSessionControl control, ISimpleAudioVolume volumeControl)
        {
            Key = key;
            ProcessId = processId;
            ProcessName = processName;
            State = state;
            Volume = volume;
            Mute = mute;
            _control = control;
            VolumeControl = volumeControl;
        }

        public void Dispose()
        {
            ComUtil.SafeRelease(_control);
            _control = null;
        }
    }
}
