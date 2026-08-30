using AiMovieReviewLab.Core;

namespace AiMovieReviewLab;

public sealed class PromptEditorForm : Form
{
    private readonly PromptStore _store;
    private readonly string _historyPrefix;
    private readonly RichTextBox _editor = new()
    {
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10f),
        AcceptsTab = true,
        WordWrap = false
    };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };

    public PromptEditorForm(string title, string helpText, PromptStore store, string activeTemplate, string historyPrefix)
    {
        _store = store;
        _historyPrefix = historyPrefix;
        Text = title;
        Width = 1120;
        Height = 820;
        MinimumSize = new Size(850, 620);
        StartPosition = FormStartPosition.CenterParent;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = helpText,
            Padding = new Padding(4),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        root.Controls.Add(_editor, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.Controls.Add(bottom, 0, 2);

        _status.Text = _store.HasCustom ? $"当前：自定义 Prompt｜{_store.CustomPath}" : "当前：内置默认 Prompt";
        bottom.Controls.Add(_status, 0, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Fill };
        var import = new Button { Text = "导入 TXT/MD", AutoSize = true };
        var export = new Button { Text = "导出", AutoSize = true };
        var reset = new Button { Text = "载入默认", AutoSize = true };
        var save = new Button { Text = "保存并使用", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true };
        buttons.Controls.AddRange([import, export, reset, save, cancel]);
        bottom.Controls.Add(buttons, 1, 0);

        _editor.Text = activeTemplate;
        import.Click += (_, _) => ImportText();
        export.Click += (_, _) => ExportText();
        reset.Click += (_, _) => { _editor.Text = _store.LoadDefault(); _status.Text = "已载入内置默认，尚未保存。"; };
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void ImportText()
    {
        using var dialog = new OpenFileDialog { Filter = "Prompt 文本|*.txt;*.md|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _editor.Text = File.ReadAllText(dialog.FileName);
        _status.Text = "已导入，尚未保存。";
    }

    private void ExportText()
    {
        using var dialog = new SaveFileDialog { Filter = "文本文件|*.txt|Markdown|*.md|所有文件|*.*", FileName = $"{_historyPrefix}_prompt.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, _editor.Text);
        _status.Text = "已导出当前编辑内容。";
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(_editor.Text))
        {
            MessageBox.Show(this, "Prompt 不能为空。", "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _store.SaveCustom(_editor.Text, _historyPrefix);
        DialogResult = DialogResult.OK;
        Close();
    }
}
