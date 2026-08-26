using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeTypeAudioGuard;

internal sealed class AudioGuardService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly AppLogger _log;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _captureGate = new();
    private readonly List<CaptureManagerRegistration> _captureManagers = new();
    private readonly Dictionary<string, CaptureSessionRegistration> _captureSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SavedSession> _original = new(StringComparer.Ordinal);
    private static Guid ChangeContext = new("E4056C42-CDE2-4945-B786-38D5626F8F01");
    private volatile bool _isCapturing;
    private int _activePercent = 100;
    private int _disposed;

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

    public void NotifySettingsChanged() => _wake.Set();

    private void Run()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            RegisterCaptureMonitoring(enumerator);
            _log.Write($"[START] event-driven capture monitor ready, wetypeSessions={GetCaptureSessionCount()}");

            // Process an already-active WeType capture session immediately if the app
            // was started while voice input was in progress.
            _wake.Set();

            while (!_cts.IsCancellationRequested)
            {
                _wake.WaitOne();
                if (_cts.IsCancellationRequested) break;
                ProcessRequestedState(enumerator);
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

            UnregisterCaptureMonitoring();
            ComUtil.SafeRelease(enumerator);
            _log.Write("[STOP] audio guard service stopped");
        }
    }

    private void ProcessRequestedState(IMMDeviceEnumerator enumerator)
    {
        var settings = _settings();
        bool captureWanted = settings.Enabled && IsAnyWeTypeCaptureActive();

        if (captureWanted && !_isCapturing)
        {
            BeginCapture(enumerator, settings);
            return;
        }

        if (!captureWanted && _isCapturing)
        {
            EndCapture(enumerator);
            return;
        }

        if (captureWanted && _isCapturing && settings.VoicePercent != _activePercent)
        {
            _activePercent = Math.Clamp(settings.VoicePercent, 1, 100);
            ProtectRenderSessions(enumerator, settings, allowNewSessions: false, reason: "settings-change");
        }
    }

    private void BeginCapture(IMMDeviceEnumerator enumerator, AppSettings settings)
    {
        _original.Clear();
        _activePercent = Math.Clamp(settings.VoicePercent, 1, 100);
        _isCapturing = true;
        _log.Write($"[VOICE START] event-driven target={_activePercent}%");
        RaiseStateChanged(true);

        // WeType's capture activation and render-session Mute happen almost together,
        // but Windows does not guarantee their callback order. Do one immediate pass,
        // then three very short retries. After ~60 ms the guard stops touching render
        // sessions completely until voice input ends.
        ProtectRenderSessions(enumerator, settings, allowNewSessions: true, reason: "0ms");

        int[] delays = { 8, 16, 36 }; // cumulative: 8 ms, 24 ms, 60 ms
        foreach (int delay in delays)
        {
            if (_cts.Token.WaitHandle.WaitOne(delay)) return;
            if (!_settings().Enabled || !IsAnyWeTypeCaptureActive()) break;

            var latest = _settings();
            _activePercent = Math.Clamp(latest.VoicePercent, 1, 100);
            ProtectRenderSessions(enumerator, latest, allowNewSessions: true, reason: $"retry+{delay}ms");
        }
    }

    private void EndCapture(IMMDeviceEnumerator enumerator)
    {
        RestoreOriginal(enumerator, "voice-end");
        _original.Clear();
        _isCapturing = false;
        _log.Write("[VOICE END] original audio state restored; guard idle");
        RaiseStateChanged(false);
    }

    private void ProtectRenderSessions(IMMDeviceEnumerator enumerator, AppSettings settings,
        bool allowNewSessions, string reason)
    {
        float factor = Math.Clamp(settings.VoicePercent, 1, 100) / 100f;

        foreach (var live in EnumerateRenderSessions(enumerator))
        {
            try
            {
                if (live.ProcessId == (uint)Environment.ProcessId || IsWeTypeProcess(live.ProcessName))
                    continue;

                // Only touch sessions that are actually active. This avoids waking or
                // changing unrelated muted/inactive applications.
                if (live.State != AudioSessionState.Active)
                    continue;

                if (!_original.TryGetValue(live.Key, out var original))
                {
                    if (!allowNewSessions) continue;

                    // If the first event-driven pass arrives just after WeType's Mute,
                    // an active muted session is assumed to have been audible immediately
                    // before voice activation. No long-term render-session monitoring is used.
                    bool assumedOriginalMute = live.Mute ? false : live.Mute;
                    original = new SavedSession(
                        live.Key,
                        live.ProcessId,
                        live.ProcessName,
                        live.Volume,
                        assumedOriginalMute);
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
                {
                    _log.Write($"[PROTECT {reason}] {live.ProcessName} pid={live.ProcessId} " +
                               $"mute={live.Mute}->False vol={live.Volume:0.00}->{target:0.00}");
                }
            }
            catch (Exception ex)
            {
                _log.Write($"[WARN] protect({reason}) failed: {ex.Message}");
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

    private void RegisterCaptureMonitoring(IMMDeviceEnumerator enumerator)
    {
        var seenDevices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in new[] { ERole.eMultimedia, ERole.eConsole, ERole.eCommunications })
        {
            IMMDevice? device = null;
            object? managerObj = null;
            IAudioSessionEnumerator? sessions = null;
            bool managerOwned = false;

            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, role, out device) < 0 || device is null)
                    continue;

                device.GetId(out string deviceId);
                if (!seenDevices.Add(deviceId)) continue;

                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out managerObj) < 0 || managerObj is null)
                    continue;

                var manager = (IAudioSessionManager2)managerObj;
                var notification = new CaptureSessionNotification(this);
                IntPtr notificationPtr = Marshal.GetComInterfaceForObject(notification, typeof(IAudioSessionNotification));
                try
                {
                    int hr = manager.RegisterSessionNotification(notificationPtr);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                }
                finally
                {
                    Marshal.Release(notificationPtr);
                }

                var managerRegistration = new CaptureManagerRegistration(deviceId, manager, notification);
                lock (_captureGate) _captureManagers.Add(managerRegistration);
                managerOwned = true;

                // Microsoft requires GetCount to be called so the manager starts
                // delivering new-session notifications without an initialization race.
                if (manager.GetSessionEnumerator(out sessions) >= 0 && sessions is not null)
                {
                    sessions.GetCount(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        IAudioSessionControl? control = null;
                        if (sessions.GetSession(i, out control) < 0 || control is null) continue;

                        bool attached = false;
                        try
                        {
                            attached = TryAttachWeTypeCaptureSession(control);
                        }
                        finally
                        {
                            if (!attached) ComUtil.SafeRelease(control);
                        }
                    }
                }

                _log.Write($"[CAPTURE DEVICE] monitoring {deviceId}");
            }
            catch (Exception ex)
            {
                _log.Write($"[WARN] capture monitor init failed: {ex.Message}");
            }
            finally
            {
                ComUtil.SafeRelease(sessions);
                if (!managerOwned) ComUtil.SafeRelease(managerObj);
                ComUtil.SafeRelease(device);
            }
        }
    }

    internal bool TryAttachWeTypeCaptureSession(IAudioSessionControl control)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;

        try
        {
            var ctl2 = control as IAudioSessionControl2;
            if (ctl2 is null) return false;

            ctl2.GetProcessId(out uint pid);
            string processName = GetProcessName(pid);
            if (!IsWeTypeProcess(processName)) return false;

            ctl2.GetSessionInstanceIdentifier(out string instanceId);
            ctl2.GetSessionIdentifier(out string sessionId);
            string key = !string.IsNullOrWhiteSpace(instanceId)
                ? instanceId
                : (!string.IsNullOrWhiteSpace(sessionId) ? sessionId : $"wetype:{pid}:{Guid.NewGuid():N}");

            control.GetState(out var initialState);
            var callback = new CaptureSessionEvents(this, key);
            var registration = new CaptureSessionRegistration(key, pid, processName, control, callback, initialState);

            lock (_captureGate)
            {
                if (_captureSessions.ContainsKey(key)) return false;
                _captureSessions[key] = registration;
            }

            IntPtr callbackPtr = Marshal.GetComInterfaceForObject(callback, typeof(IAudioSessionEvents));
            try
            {
                int hr = control.RegisterAudioSessionNotification(callbackPtr);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
            catch
            {
                lock (_captureGate) _captureSessions.Remove(key);
                throw;
            }
            finally
            {
                Marshal.Release(callbackPtr);
            }

            _log.Write($"[WETYPE SESSION] attached {processName} pid={pid} state={initialState}");
            _wake.Set();
            return true;
        }
        catch (Exception ex)
        {
            _log.Write($"[WARN] attach WeType capture session failed: {ex.Message}");
            return false;
        }
    }

    internal void OnWeTypeCaptureStateChanged(string key, AudioSessionState state)
    {
        lock (_captureGate)
        {
            if (_captureSessions.TryGetValue(key, out var registration))
                registration.State = state;
        }

        _log.Write($"[WETYPE STATE] {state}");
        _wake.Set();
    }

    internal void OnWeTypeCaptureDisconnected(string key, AudioSessionDisconnectReason reason)
    {
        lock (_captureGate)
        {
            if (_captureSessions.TryGetValue(key, out var registration))
                registration.State = AudioSessionState.Expired;
        }

        _log.Write($"[WETYPE DISCONNECT] {reason}");
        _wake.Set();
    }

    private bool IsAnyWeTypeCaptureActive()
    {
        lock (_captureGate)
        {
            foreach (var registration in _captureSessions.Values)
            {
                if (registration.State == AudioSessionState.Active)
                    return true;
            }
        }
        return false;
    }

    private int GetCaptureSessionCount()
    {
        lock (_captureGate) return _captureSessions.Count;
    }

    private void UnregisterCaptureMonitoring()
    {
        List<CaptureSessionRegistration> sessions;
        List<CaptureManagerRegistration> managers;

        lock (_captureGate)
        {
            sessions = _captureSessions.Values.ToList();
            managers = _captureManagers.ToList();
            _captureSessions.Clear();
            _captureManagers.Clear();
        }

        foreach (var registration in sessions)
        {
            try
            {
                IntPtr ptr = Marshal.GetComInterfaceForObject(registration.Callback, typeof(IAudioSessionEvents));
                try { registration.Control.UnregisterAudioSessionNotification(ptr); }
                finally { Marshal.Release(ptr); }
            }
            catch { }
            ComUtil.SafeRelease(registration.Control);
        }

        foreach (var registration in managers)
        {
            try
            {
                IntPtr ptr = Marshal.GetComInterfaceForObject(registration.Callback, typeof(IAudioSessionNotification));
                try { registration.Manager.UnregisterSessionNotification(ptr); }
                finally { Marshal.Release(ptr); }
            }
            catch { }
            ComUtil.SafeRelease(registration.Manager);
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
        // audiodg is the Windows audio engine/APO host. It can be muted by WeType and
        // therefore needs unmuting, but scaling it as well as the player would multiply
        // attenuation (for example 30% x 30%). System Sounds are treated the same way.
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        _wake.Set();
        if (!_thread.Join(2500))
            _log.Write("[WARN] service thread did not stop within timeout");

        _wake.Dispose();
        _cts.Dispose();
    }

    private sealed record SavedSession(string Key, uint ProcessId, string ProcessName, float Volume, bool Mute);

    private sealed class CaptureManagerRegistration
    {
        public string DeviceId { get; }
        public IAudioSessionManager2 Manager { get; }
        public CaptureSessionNotification Callback { get; }

        public CaptureManagerRegistration(string deviceId, IAudioSessionManager2 manager, CaptureSessionNotification callback)
        {
            DeviceId = deviceId;
            Manager = manager;
            Callback = callback;
        }
    }

    private sealed class CaptureSessionRegistration
    {
        public string Key { get; }
        public uint ProcessId { get; }
        public string ProcessName { get; }
        public IAudioSessionControl Control { get; }
        public CaptureSessionEvents Callback { get; }
        public AudioSessionState State { get; set; }

        public CaptureSessionRegistration(string key, uint processId, string processName,
            IAudioSessionControl control, CaptureSessionEvents callback, AudioSessionState state)
        {
            Key = key;
            ProcessId = processId;
            ProcessName = processName;
            Control = control;
            Callback = callback;
            State = state;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CaptureSessionNotification : IAudioSessionNotification
    {
        private readonly AudioGuardService _owner;
        public CaptureSessionNotification(AudioGuardService owner) => _owner = owner;

        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            try { _owner.TryAttachWeTypeCaptureSession(newSession); } catch { }
            return 0;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CaptureSessionEvents : IAudioSessionEvents
    {
        private readonly AudioGuardService _owner;
        private readonly string _key;

        public CaptureSessionEvents(AudioGuardService owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public int OnDisplayNameChanged(string newDisplayName, IntPtr eventContext) => 0;
        public int OnIconPathChanged(string newIconPath, IntPtr eventContext) => 0;
        public int OnSimpleVolumeChanged(float newVolume, bool newMute, IntPtr eventContext) => 0;
        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, IntPtr eventContext) => 0;
        public int OnGroupingParamChanged(IntPtr newGroupingParam, IntPtr eventContext) => 0;

        public int OnStateChanged(AudioSessionState newState)
        {
            _owner.OnWeTypeCaptureStateChanged(_key, newState);
            return 0;
        }

        public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            _owner.OnWeTypeCaptureDisconnected(_key, disconnectReason);
            return 0;
        }
    }

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
