namespace AiMovieReviewLab;

public sealed partial class MainForm
{
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _questions.Resize += (_, _) => ResizeQuestionCards();
        ResizeQuestionCards();
    }
}
