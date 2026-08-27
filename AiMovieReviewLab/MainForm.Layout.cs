namespace AiMovieReviewLab;

public sealed partial class MainForm
{
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        Text = $"AI 观影短评实验台 v{Application.ProductVersion}｜豆瓣事实定位 · 多模型 · 三轮采访";

        var root = Controls.OfType<SplitContainer>().FirstOrDefault(x => x.Name == "RootSplit");
        if (root is not null)
        {
            ApplyRootSplit(root);
            root.SizeChanged += (_, _) => ApplyRootSplit(root);
        }

        _questions.Resize += (_, _) => ResizeQuestionCards();
        ResizeQuestionCards();
    }

    private static void ApplyRootSplit(SplitContainer root)
    {
        var available = root.ClientSize.Width - root.SplitterWidth;
        if (available <= 2) return;

        var desiredRight = Math.Min(420, Math.Max(280, available / 3));
        var desiredLeft = Math.Min(900, Math.Max(1, available - desiredRight));
        var maxDistance = Math.Max(1, available - 1);
        var safeDistance = Math.Clamp(desiredLeft, 1, maxDistance);

        if (root.SplitterDistance != safeDistance)
            root.SplitterDistance = safeDistance;
    }
}
