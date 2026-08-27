namespace AiMovieReviewLab.Core;

public sealed class PromptStore
{
    private const string RuntimeGuardStart = "<!-- PROGRAM_RUNTIME_GUARD_START -->";
    private const string RuntimeGuardEnd = "<!-- PROGRAM_RUNTIME_GUARD_END -->";

    private readonly string _defaultPath;
    private readonly string _defaultFileName;
    private readonly string _customPath;
    private readonly string _historyDir;

    public PromptStore(string defaultFileName, string customFileName)
    {
        _defaultFileName = defaultFileName;
        _defaultPath = Path.Combine(AppContext.BaseDirectory, "Prompts", defaultFileName);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiMovieReviewLab", "Prompts");
        Directory.CreateDirectory(root);
        _customPath = Path.Combine(root, customFileName);
        _historyDir = Path.Combine(root, "History");
        Directory.CreateDirectory(_historyDir);
    }

    public string CustomPath => _customPath;
    public bool HasCustom => File.Exists(_customPath);

    public string LoadActive()
    {
        var raw = HasCustom && !string.IsNullOrWhiteSpace(File.ReadAllText(_customPath))
            ? File.ReadAllText(_customPath)
            : LoadDefaultRaw();
        return AppendRuntimeGuard(StripRuntimeGuard(raw));
    }

    public string LoadDefault() => AppendRuntimeGuard(StripRuntimeGuard(LoadDefaultRaw()));

    private string LoadDefaultRaw()
    {
        if (File.Exists(_defaultPath)) return File.ReadAllText(_defaultPath);
        return "你是一名电影观后感采访编辑。只输出程序要求的 JSON。";
    }

    public void SaveCustom(string text, string historyPrefix)
    {
        text = StripRuntimeGuard(text).Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Prompt 不能为空。 ");
        if (File.Exists(_customPath))
        {
            var previous = StripRuntimeGuard(File.ReadAllText(_customPath));
            if (!string.IsNullOrWhiteSpace(previous))
            {
                var backup = Path.Combine(_historyDir, $"{historyPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(backup, previous);
            }
        }
        File.WriteAllText(_customPath, text);
    }

    public void Reset() { if (File.Exists(_customPath)) File.Delete(_customPath); }

    private string AppendRuntimeGuard(string prompt)
    {
        var guard = _defaultFileName.Equals("interview_prompt_default.txt", StringComparison.OrdinalIgnoreCase)
            ? InterviewRuntimeGuard
            : _defaultFileName.Equals("review_prompt_default.txt", StringComparison.OrdinalIgnoreCase)
                ? ReviewRuntimeGuard
                : string.Empty;

        if (string.IsNullOrWhiteSpace(guard)) return prompt.Trim();
        return prompt.TrimEnd() + Environment.NewLine + Environment.NewLine
               + RuntimeGuardStart + Environment.NewLine
               + guard.Trim() + Environment.NewLine
               + RuntimeGuardEnd;
    }

    private static string StripRuntimeGuard(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var start = text.IndexOf(RuntimeGuardStart, StringComparison.Ordinal);
        if (start < 0) return text;
        var end = text.IndexOf(RuntimeGuardEnd, start, StringComparison.Ordinal);
        if (end < 0) return text[..start];
        end += RuntimeGuardEnd.Length;
        return (text[..start] + text[end..]).Trim();
    }

    private const string InterviewRuntimeGuard = """
# 程序级跨轮采访状态机（硬规则，优先于上面的软性写作建议）

你会在第二、第三轮收到此前完整 INTERVIEW_TRANSCRIPT。不要只把它当聊天记录，要先把每一题归入以下状态，再生成新题：

1. USER_AUTHORED：用户自由补充中的每一句话都是最高权重的用户原话。不得反驳、改写成相反偏好，也不得从“纠正AI前提”擅自推出一个新的正面观点。
2. CLOSED：如果某题用户勾选“都不符合”，表示这道题的提问框架以及 A/B/C 三个候选方向都没有命中。它不是“信息不足”，而是“这个问法已经被否定”。后续轮次禁止换措辞重问语义等价的问题。
3. CORRECTED：如果用户在“都不符合”后写了自由文字，这段自由文字是在纠正/替换原问题的前提。原问题前提立即作废，后续不得继续沿用；只能从用户新写出的内容出发。
4. ANSWERED：用户已经勾选 A/B/C 或给出自由文字的题，视为已经取得该信息。后续可以沿它取得“新的材料”，但不能再问一个大概率得到同一份答案的问题。

特别重要：
- “都不符合”之后不要再问“是不是其实因为……”“换个角度是不是……”。这仍然是在重复。
- 用户纠正一个错误前提，只代表该前提应被删除，不代表用户喜欢它的反面。例如用户说“这电影不是喜剧片，从头到尾没有喜剧元素”，这只是在纠正“喜剧/搞笑预期”的错误前提；后续禁止再问“不搞笑反而更真实”“沈腾以往喜剧形象的反差”“去掉搞笑包袱”等，也不能据此推断用户特别欣赏“严肃基调”。
- 用户提到“闪回很好”后，如果已经有一题专门追问闪回的作用，而用户选择“都不符合”，后续就必须放弃“闪回为什么好/闪回如何影响台词”这一问题族，不能第三轮再换句话追问。
- 第一轮也禁止凭大众印象给问题加前提，例如“通常大家看沈腾是为了开心”“一般观众会期待某演员搞笑”。除非用户本人主动提到这种预期，否则这种前提不属于用户。

生成新一轮前，内部做一次语义去重：
- 列出所有 CLOSED/CORRECTED 的问题族：绝不再问；
- 列出所有 ANSWERED 的信息目标：绝不换措辞重问；
- 三个新问题必须各自取得一份尚未得到的新信息。

如果上一轮可追的新线索不足以支持3个深挖问题，可以转向用户已经主动表达过但尚未问过的整体体验、评分边界、记忆点、推荐意愿或最终余味；不要为了凑3题死磕同一个结尾、同一句台词或同一个闪回。
""";

    private const string ReviewRuntimeGuard = """
# 程序级最终短评排除规则（硬规则）

在整理短评前，必须把采访答案分成“可用观点”和“排除信号”：

1. 某题如果用户勾选“都不符合”，该题的问题前提以及 A/B/C 三个选项都不是用户观点，全部禁止写入最终短评。
2. 如果“都不符合”同时带有自由补充，自由补充是最高权重的纠正；原问题前提必须彻底删除，不能在短评里换一种更漂亮的说法复活。
3. 用户自由文字是在纠正AI时，不要把“纠正”反向包装成赞美。例如用户说“这电影不是喜剧片，从头到尾没有喜剧元素”，禁止写“不是沈腾惯有的幽默”“剥离搞笑伪装”“不搞笑但更真实”“打破喜剧预期”等；这些都仍然来自被用户否定的AI前提。
4. 只有用户实际勾选且未被自由文字推翻的 A/B/C，才可以作为低于自由文字的辅助信号。
5. 如果后续问题本身建立在早先被纠正的错误前提上，即使用户勾选了其中某项，也要优先服从更早/更明确的用户自由文字，不要让错误前提通过后续选项重新污染短评。
6. “都不符合”不是一句可以写进短评的观点；它的唯一作用是告诉你哪些方向不要写。

输出前逐句检查：这句话若删掉所有用户自由文字与真实勾选，只靠AI问题措辞、AI选项或网络解读还能成立吗？如果是，就删除。
""";
}
