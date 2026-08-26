using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeTypeAudioGuard;

internal sealed class EventAudioGuardService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly AppLogger _log;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);

    private readonly object _captureGate = new();
    private readonly List<CaptureManagerRegistration> _captureManagers = new();
    private readonly Dictionary<string, CaptureSessionRegistration> _captureSessions = new(StringComparer.Ordinal);

    private readonly object _renderGate = new();
    private readonly Dictionary<string, RenderWatch> _renderWatches = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RenderVolumeEvent> _renderEvents = new();

    private static Guid ChangeContext = new("E4056C42-CDE2-4945-B786-38D5626F8F01");
    private volatile bool _isCapturing;
    private int _activePercent = 100;
    private int _disposed;

    public bool IsCapturing => _isCapturing;
    public event Action<bool>? CaptureStateChanged;

    public EventAudioGuardService(Func<AppSettings> settings, AppLogger log)
    {
        _settings = settings;
        _log = log;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WeTypeAudioGuard.EventCore"
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
            _log.Write($"[START] event render guard ready, wetypeSessions={GetCaptureSessionCount()}");
            _wake.Set();

            while (!_cts.IsCancellationRequested)
            {
                _wake.WaitOne();
                if (_cts.IsCancellationRequested) break;

                ProcessRenderEvents();
                ProcessRequestedState(enumerator);
                ProcessRenderEvents();
            }
        }
        catch (Exception ex)
        {
            _log.Write("[FATAL] " + ex);
        }
        finally
        {
            try { StopRenderWatching(restore: true, reason: "service-stop"); } catch { }
            UnregisterCaptureMonitoring();
            ComUtil.SafeRelease(enumerator);
            _log.Write("[STOP] event render guard stopped");
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
            EndCapture();
            return;
        }

        if (captureWanted && _isCapturing && settings.VoicePercent != _activePercent)
        {
            _activePercent = Math.Clamp(settings.VoicePercent, 1, 100);
            ApplyVoicePercentToWatches(_activePercent);
        }
    }

    private void BeginCapture(IMMDeviceEnumerator enumerator, AppSettings settings)
    {
        _activePercent = Math.Clamp(settings.VoicePercent, 1, 100);
        _isCapturing = true;

        int watched = StartRenderWatching(enumerator, _activePercent);
        _log.Write($"[VOICE START] event-driven target={_activePercent}% renderWatches={watched}");
        RaiseStateChanged(true);
    }

    private void EndCapture()
    {
        StopRenderWatching(restore: true, reason: "voice-end");
        _isCapturing = false;
        while (_renderEvents.TryDequeue(out _)) { }
        _log.Write("[VOICE END] temporary render watchers removed; original state restored");
        RaiseStateChanged(false);
    }

    private int StartRenderWatching(IMMDeviceEnumerator enumerator, int percent)
    {
        StopRenderWatching(restore: false, reason: "restart");

        float factor = percent / 100f;
        var candidates = GetActiveRenderSessions(enumerator);
        int attached = 0;

        foreach (var candidate in candidates)
        {
            bool keepControl = false;
            try
            {
                if (candidate.ProcessId == (uint)Environment.ProcessId || IsWeTypeProcess(candidate.ProcessName))
                    continue;

                // If WeType happened to mute the session just before this callback was
                // delivered, treat an ACTIVE muted session as previously audible.
                bool originalMute = candidate.Mute ? false : candidate.Mute;
                bool scaleVolume = ShouldScaleVolume(candidate.ProcessName, candidate.ProcessId);
                float target = scaleVolume
                    ? Math.Clamp(candidate.Volume * factor, 0f, 1f)
                    : candidate.Volume;

                var callback = new RenderSessionEvents(this, candidate.Key);
                var watch = new RenderWatch(
                    candidate.Key,
                    candidate.ProcessId,
                    candidate.ProcessName,
                    candidate.Control,
                    candidate.VolumeControl,
                    callback,
                    candidate.Volume,
                    originalMute,
                    scaleVolume,
                    target);

                IntPtr callbackPtr = Marshal.GetComInterfaceForObject(callback, typeof(IAudioSessionEvents));
                try
                {
                    int hr = candidate.Control.RegisterAudioSessionNotification(callbackPtr);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                }
                finally
                {
                    Marshal.Release(callbackPtr);
                }

                lock (_renderGate)
                    _renderWatches[candidate.Key] = watch;

                keepControl = true;
                attached++;

                // Apply the selected attenuation once at voice start. At 100% this is
                // a no-op. Also repair a mute that raced ahead of watcher registration.
                if (candidate.Mute)
                    candidate.VolumeControl.SetMute(false, ref ChangeContext);

                if (scaleVolume && Math.Abs(candidate.Volume - target) > 0.01f)
                    candidate.VolumeControl.SetMasterVolume(target, ref ChangeContext);

                _log.Write($"[RENDER WATCH] {candidate.ProcessName} pid={candidate.ProcessId} " +
                           $"mute={candidate.Mute} vol={candidate.Volume:0.00} target={target:0.00}");
            }
            catch (Exception ex)
            {
                _log.Write($"[WARN] render watch attach failed: {ex.Message}");
            }
            finally
            {
                if (!keepControl)
                    ComUtil.SafeRelease(candidate.Control);
            }
        }

        return attached;
    }

    private void ProcessRenderEvents()
    {
        while (_renderEvents.TryDequeue(out var evt))
        {
            if (!_isCapturing) continue;

            RenderWatch? watch;
            lock (_renderGate)
                _renderWatches.TryGetValue(evt.Key, out watch);

            if (watch is null) continue;
            if (evt.OurEvent) continue;

            try
            {
                if (evt.NewMute)
                {
                    watch.VolumeControl.GetMute(out bool muteNow);
                    if (muteNow)
                    {
                        watch.VolumeControl.SetMute(false, ref ChangeContext);
                        _log.Write($"[MUTE BLOCK] {watch.ProcessName} pid={watch.ProcessId} True->False");
                    }
                }

                // WeType has only been observed changing Mute, not the session volume.
                // Keep the user-selected attenuation if some external change occurs
                // during the short voice session.
                if (watch.ScaleVolume)
                {
                    watch.VolumeControl.GetMasterVolume(out float volumeNow);
                    if (Math.Abs(volumeNow - watch.TargetVolume) > 0.01f)
                        watch.VolumeControl.SetMasterVolume(watch.TargetVolume, ref ChangeContext);
                }
            }
            catch (Exception ex)
            {
                _log.Write($"[WARN] render event repair failed: {ex.Message}");
            }
        }
    }

    private void ApplyVoicePercentToWatches(int percent)
    {
        float factor = percent / 100f;
        List<RenderWatch> watches;
        lock (_renderGate) watches = _renderWatches.Values.ToList();

        foreach (var watch in watches)
        {
            try
            {
                watch.TargetVolume = watch.ScaleVolume
                    ? Math.Clamp(watch.OriginalVolume * factor, 0f, 1f)
                    : watch.OriginalVolume;

                if (watch.ScaleVolume)
                    watch.VolumeControl.SetMasterVolume(watch.TargetVolume, ref ChangeContext);
            }
            catch (Exception ex)
            {
                _log.Write($"[WARN] settings volume update failed: {ex.Message}");
            }
        }
    }

    private void StopRenderWatching(bool restore, string reason)
    {
        List<RenderWatch> watches;
        lock (_renderGate)
        {
            watches = _renderWatches.Values.ToList();
            _renderWatches.Clear();
        }

        foreach (var watch in watches)
        {
            try
            {
                IntPtr ptr = Marshal.GetComInterfaceForObject(watch.Callback, typeof(IAudioSessionEvents));
                try { watch.Control.UnregisterAudioSessionNotification(ptr); }
                finally { Marshal.Release(ptr); }
            }
            catch { }

            if (restore)
            {
                try
                {
                    watch.VolumeControl.GetMasterVolume(out float currentVolume);
                    if (Math.Abs(currentVolume - watch.OriginalVolume) > 0.01f)
                        watch.VolumeControl.SetMasterVolume(watch.OriginalVolume, ref ChangeContext);

                    watch.VolumeControl.GetMute(out bool currentMute);
                    if (currentMute != watch.OriginalMute)
                        watch.VolumeControl.SetMute(watch.OriginalMute, ref ChangeContext);
                }
                catch (Exception ex)
                {
                    _log.Write($"[WARN] restore({reason}) failed for {watch.ProcessName}: {ex.Message}");
                }
            }

            ComUtil.SafeRelease(watch.Control);
        }
    }

    internal void OnRenderVolumeChanged(string key, float newVolume, bool newMute, IntPtr eventContext)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_isCapturing) return;
        _renderEvents.Enqueue(new RenderVolumeEvent(key, newVolume, newMute, IsOurEventContext(eventContext)));
        _wake.Set();
    }

    private static bool IsOurEventContext(IntPtr eventContext)
    {
        if (eventContext == IntPtr.Zero) return false;
        try
        {
            Guid value = Marshal.PtrToStructure<Guid>(eventContext);
            return value == ChangeContext;
        }
        catch
        {
            return false;
        }
    }

    private List<RenderCandidate> GetActiveRenderSessions(IMMDeviceEnumerator enumerator)
    {
        var result = new List<RenderCandidate>();
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

                    bool transferred = false;
                    try
                    {
                        var ctl2 = control as IAudioSessionControl2;
                        var volume = control as ISimpleAudioVolume;
                        if (ctl2 is null || volume is null) continue;

                        control.GetState(out var state);
                        if (state != AudioSessionState.Active) continue;

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

                        result.Add(new RenderCandidate(key, pid, processName, control, volume, level, mute));
                        transferred = true;
                    }
                    finally
                    {
                        if (!transferred) ComUtil.SafeRelease(control);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write($"[WARN] enumerate render failed: {ex.Message}");
            }
            finally
            {
                ComUtil.SafeRelease(sessions);
                ComUtil.SafeRelease(managerObj);
                ComUtil.SafeRelease(device);
            }
        }

        return result;
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

                lock (_captureGate)
                    _captureManagers.Add(new CaptureManagerRegistration(deviceId, manager, notification));
                managerOwned = true;

                if (manager.GetSessionEnumerator(out sessions) >= 0 && sessions is not null)
                {
                    sessions.GetCount(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        IAudioSessionControl? control = null;
                        if (sessions.GetSession(i, out control) < 0 || control is null) continue;

                        bool attached = false;
                        try { attached = TryAttachWeTypeCaptureSession(control); }
                        finally { if (!attached) ComUtil.SafeRelease(control); }
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
            return _captureSessions.Values.Any(x => x.State == AudioSessionState.Active);
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

    private static bool IsWeTypeProcess(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.StartsWith("wetype_", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldScaleVolume(string processName, uint pid)
    {
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
            _log.Write("[WARN] event render guard thread did not stop within timeout");
        _wake.Dispose();
        _cts.Dispose();
    }

    private sealed record RenderCandidate(
        string Key,
        uint ProcessId,
        string ProcessName,
        IAudioSessionControl Control,
        ISimpleAudioVolume VolumeControl,
        float Volume,
        bool Mute);

    private sealed class RenderWatch
    {
        public string Key { get; }
        public uint ProcessId { get; }
        public string ProcessName { get; }
        public IAudioSessionControl Control { get; }
        public ISimpleAudioVolume VolumeControl { get; }
        public RenderSessionEvents Callback { get; }
        public float OriginalVolume { get; }
        public bool OriginalMute { get; }
        public bool ScaleVolume { get; }
        public float TargetVolume { get; set; }

        public RenderWatch(string key, uint processId, string processName,
            IAudioSessionControl control, ISimpleAudioVolume volumeControl,
            RenderSessionEvents callback, float originalVolume, bool originalMute,
            bool scaleVolume, float targetVolume)
        {
            Key = key;
            ProcessId = processId;
            ProcessName = processName;
            Control = control;
            VolumeControl = volumeControl;
            Callback = callback;
            OriginalVolume = originalVolume;
            OriginalMute = originalMute;
            ScaleVolume = scaleVolume;
            TargetVolume = targetVolume;
        }
    }

    private sealed record RenderVolumeEvent(string Key, float NewVolume, bool NewMute, bool OurEvent);

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
    private sealed class RenderSessionEvents : IAudioSessionEvents
    {
        private readonly EventAudioGuardService _owner;
        private readonly string _key;

        public RenderSessionEvents(EventAudioGuardService owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public int OnDisplayNameChanged(string newDisplayName, IntPtr eventContext) => 0;
        public int OnIconPathChanged(string newIconPath, IntPtr eventContext) => 0;
        public int OnSimpleVolumeChanged(float newVolume, bool newMute, IntPtr eventContext)
        {
            try { _owner.OnRenderVolumeChanged(_key, newVolume, newMute, eventContext); } catch { }
            return 0;
        }
        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, IntPtr eventContext) => 0;
        public int OnGroupingParamChanged(IntPtr newGroupingParam, IntPtr eventContext) => 0;
        public int OnStateChanged(AudioSessionState newState) => 0;
        public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => 0;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CaptureSessionNotification : IAudioSessionNotification
    {
        private readonly EventAudioGuardService _owner;
        public CaptureSessionNotification(EventAudioGuardService owner) => _owner = owner;

        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            try { _owner.TryAttachWeTypeCaptureSession(newSession); } catch { }
            return 0;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CaptureSessionEvents : IAudioSessionEvents
    {
        private readonly EventAudioGuardService _owner;
        private readonly string _key;

        public CaptureSessionEvents(EventAudioGuardService owner, string key)
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
            try { _owner.OnWeTypeCaptureStateChanged(_key, newState); } catch { }
            return 0;
        }
        public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            try { _owner.OnWeTypeCaptureDisconnected(_key, disconnectReason); } catch { }
            return 0;
        }
    }
}
