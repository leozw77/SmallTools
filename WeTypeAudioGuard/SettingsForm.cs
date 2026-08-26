namespace WeTypeAudioGuard;

internal sealed class SettingsForm : Form
{
    private readonly CheckBox _enabled = new() { Text = "启用保护", AutoSize = true };
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly NumericUpDown _custom = new() { Minimum = 1, Maximum = 100, Width = 80 };
    private readonly CheckBox _startup = new() { Text = "开机自动启动", AutoSize = true };
    private readonly CheckBox _logging = new() { Text = "记录日志（保存在程序所在文件夹）", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Button _save = new() { Text = "保存", AutoSize = true };
    private readonly Button _cancel = new() { Text = "取消", AutoSize = true };

    public event Action<AppSettings>? SettingsSaved;

    public SettingsForm(AppSettings settings)
    {
        Text = "微信输入法音频保护器";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        _preset.Items.AddRange(new object[]
        {
            "保持原音量 (100%)", "降低到 20%", "降低到 30%", "降低到 50%", "自定义"
        });

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 7,
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(_enabled, 0, 0);
        layout.SetColumnSpan(_enabled, 2);
        layout.Controls.Add(new Label { Text = "语音输入时后台声音：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_preset, 1, 1);
        layout.Controls.Add(new Label { Text = "自定义百分比：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_custom, 1, 2);
        layout.Controls.Add(_startup, 0, 3);
        layout.SetColumnSpan(_startup, 2);
        layout.Controls.Add(_logging, 0, 4);
        layout.SetColumnSpan(_logging, 2);
        layout.Controls.Add(_status, 0, 5);
        layout.SetColumnSpan(_status, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_cancel);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        _preset.SelectedIndexChanged += (_, _) => _custom.Enabled = _preset.SelectedIndex == 4;
        _save.Click += (_, _) => SaveAndClose();
        _cancel.Click += (_, _) => Close();

        LoadFrom(settings);
    }

    public void LoadFrom(AppSettings settings)
    {
        _enabled.Checked = settings.Enabled;
        _startup.Checked = settings.StartWithWindows;
        _logging.Checked = settings.LoggingEnabled;
        _custom.Value = Math.Clamp(settings.VoicePercent, 1, 100);
        _preset.SelectedIndex = settings.VoicePercent switch
        {
            100 => 0,
            20 => 1,
            30 => 2,
            50 => 3,
            _ => 4
        };
        _custom.Enabled = _preset.SelectedIndex == 4;
    }

    public void SetCaptureState(bool active)
    {
        if (IsDisposed) return;
        void Apply() => _status.Text = active ? "状态：微信输入法正在语音输入" : "状态：等待微信输入法语音";
        if (InvokeRequired) BeginInvoke((Action)Apply); else Apply();
    }

    private void SaveAndClose()
    {
        int percent = _preset.SelectedIndex switch
        {
            0 => 100,
            1 => 20,
            2 => 30,
            3 => 50,
            _ => (int)_custom.Value
        };

        SettingsSaved?.Invoke(new AppSettings
        {
            Enabled = _enabled.Checked,
            VoicePercent = percent,
            StartWithWindows = _startup.Checked,
            LoggingEnabled = _logging.Checked
        });
        Close();
    }
}
