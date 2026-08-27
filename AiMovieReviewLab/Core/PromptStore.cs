namespace AiMovieReviewLab.Core;

public sealed class PromptStore
{
    private readonly string _defaultPath;
    private readonly string _customPath;
    private readonly string _historyDir;

    public PromptStore(string defaultFileName, string customFileName)
    {
        _defaultPath = Path.Combine(AppContext.BaseDirectory, "Prompts", defaultFileName);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiMovieReviewLab", "Prompts");
        Directory.CreateDirectory(root);
        _customPath = Path.Combine(root, customFileName);
        _historyDir = Path.Combine(root, "History");
        Directory.CreateDirectory(_historyDir);
    }

    public string CustomPath => _customPath;
    public bool HasCustom => File.Exists(_customPath);

    public string LoadActive() => HasCustom && !string.IsNullOrWhiteSpace(File.ReadAllText(_customPath))
        ? File.ReadAllText(_customPath)
        : LoadDefault();

    public string LoadDefault()
    {
        if (File.Exists(_defaultPath)) return File.ReadAllText(_defaultPath);
        return "你是一名电影观后感采访编辑。只输出程序要求的 JSON。";
    }

    public void SaveCustom(string text, string historyPrefix)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Prompt 不能为空。 ");
        if (File.Exists(_customPath))
        {
            var previous = File.ReadAllText(_customPath);
            if (!string.IsNullOrWhiteSpace(previous))
            {
                var backup = Path.Combine(_historyDir, $"{historyPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(backup, previous);
            }
        }
        File.WriteAllText(_customPath, text);
    }

    public void Reset() { if (File.Exists(_customPath)) File.Delete(_customPath); }
}
