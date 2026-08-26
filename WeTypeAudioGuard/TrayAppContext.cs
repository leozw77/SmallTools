using System.Diagnostics;

namespace WeTypeAudioGuard;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly string _baseDir = AppContext.BaseDirectory;
    private readonly SettingsStore _store;
    private readonly AppLogger _logger;
    private readonly NotifyIcon _tray;
    private readonly EventAudioGuardService _service;
    private readonly SynchronizationContext _uiContext;
    private SettingsForm? _settingsForm;

    private readonly ToolStripMenuItem _enabledItem = new("已启用") { CheckOnClick = true };
    private readonly ToolStripMenuItem _p100 = new("保持原音量 (100%)");
    private readonly ToolStripMenuItem _p20 = new("20%");
    private readonly ToolStripMenuItem _p30 = new("30%");
    private readonly ToolStripMenuItem _p50 = new("50%");
    private readonly ToolStripMenuItem _startupItem = new("开机启动") { CheckOnClick = true };

    public TrayAppContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _store = new SettingsStore(_baseDir);
        _logger = new AppLogger(_baseDir, () => _store.Snapshot().LoggingEnabled);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("语音时后台声音")
        {
            DropDownItems = { _p100, _p20, _p30, _p50 }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("打开设置", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("打开程序/日志文件夹", null, (_, _) => OpenBaseFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
            Text = "微信输入法音频保护器"
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        _enabledItem.Click += (_, _) => UpdateQuick(s => s.Enabled = _enabledItem.Checked);
        _startupItem.Click += (_, _) => UpdateQuick(s => s.StartWithWindows = _startupItem.Checked);
        _p100.Click += (_, _) => SetPercent(100);
        _p20.Click += (_, _) => SetPercent(20);
        _p30.Click += (_, _) => SetPercent(30);
        _p50.Click += (_, _) => SetPercent(50);

        ApplyStartup(_store.Snapshot().StartWithWindows);
        RefreshMenu();

        _service = new EventAudioGuardService(() => _store.Snapshot(), _logger);
        _service.CaptureStateChanged += OnCaptureStateChanged;
        _logger.Write("[APP] started");
    }

    private void UpdateQuick(Action<AppSettings> mutate)
    {
        var s = _store.Snapshot();
        mutate(s);
        _store.Update(s);
        ApplyStartup(s.StartWithWindows);
        RefreshMenu();
        _settingsForm?.LoadFrom(s);
        _service.NotifySettingsChanged();
    }

    private void SetPercent(int percent)
    {
        UpdateQuick(s => s.VoicePercent = percent);
    }

    private void RefreshMenu()
    {
        var s = _store.Snapshot();
        _enabledItem.Checked = s.Enabled;
        _enabledItem.Text = s.Enabled ? "已启用" : "已停用";
        _startupItem.Checked = s.StartWithWindows;
        _p100.Checked = s.VoicePercent == 100;
        _p20.Checked = s.VoicePercent == 20;
        _p30.Checked = s.VoicePercent == 30;
        _p50.Checked = s.VoicePercent == 50;
        _tray.Text = $"微信输入法音频保护器 - {(s.Enabled ? s.VoicePercent + "%" : "已停用")}";
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_store.Snapshot());
        _settingsForm.SetCaptureState(_service.IsCapturing);
        _settingsForm.SettingsSaved += s =>
        {
            _store.Update(s);
            ApplyStartup(s.StartWithWindows);
            RefreshMenu();
            _service.NotifySettingsChanged();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void OnCaptureStateChanged(bool active)
    {
        _uiContext.Post(_ =>
        {
            try
            {
                if (_settingsForm is { IsDisposed: false }) _settingsForm.SetCaptureState(active);
                _tray.Text = active
                    ? $"微信输入法音频保护器 - 语音中 {_store.Snapshot().VoicePercent}%"
                    : $"微信输入法音频保护器 - {_store.Snapshot().VoicePercent}%";
            }
            catch { }
        }, null);
    }

    private static void ApplyStartup(bool enabled)
    {
        try { StartupManager.SetEnabled(enabled); } catch { }
    }

    private void OpenBaseFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _baseDir) { UseShellExecute = true });
        }
        catch { }
    }

    private void ExitApp()
    {
        _service.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _settingsForm?.Dispose();
        _logger.Write("[APP] exited");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _service.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
            try { _settingsForm?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
