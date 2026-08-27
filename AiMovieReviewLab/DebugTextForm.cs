namespace AiMovieReviewLab;

public sealed class DebugTextForm : Form
{
    public DebugTextForm(string title, string text)
    {
        Text = title;
        Width = 1050;
        Height = 760;
        StartPosition = FormStartPosition.CenterParent;
        var box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            Text = text
        };
        Controls.Add(box);
    }
}
