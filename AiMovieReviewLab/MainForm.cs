using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiMovieReviewLab.Core;

namespace AiMovieReviewLab;

public sealed partial class MainForm : Form
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SubtitleCleaner _subtitleCleaner = new();
    private readonly LabSettingsStore _settingsStore = new();
    private readonly PromptStore _interviewPromptStore = new("interview_prompt_default.txt", "interview_prompt_custom.txt");
    private readonly PromptStore _reviewPromptStore = new("review_prompt_default.txt", "review_prompt_custom.txt");
    private readonly OpenAiCompatibleClient _client;
    private readonly InterviewEngine _interviewEngine;
    private readonly ReviewEngine _reviewEngine;

    private readonly ComboBox _provider = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _baseUrl = new() { Dock = DockStyle.Fill };
    private readonly TextBox _model = new() { Dock = DockStyle.Fill };
    private readonly TextBox _apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "API Key（只在当前窗口内存中使用，不保存）" };
    private readonly CheckBox _webSearch = new() { AutoSize = true, Text = "联网搜索" };
    private readonly CheckBox _forceSearch = new() { AutoSize = true, Text = "无字幕时强制搜索" };
    private readonly CheckBox _thinking = new() { AutoSize = true, Text = "Thinking", Checked = false };
    private readonly NumericUpDown _inputPrice = PriceBox();
    private readonly NumericUpDown _outputPrice = PriceBox();
    private readonly NumericUpDown _cachePrice = PriceBox();

    private readonly TextBox _movieTitle = new() { Dock = DockStyle.Fill, PlaceholderText = "例如：欢迎来龙餐馆" };
    private readonly ComboBox _rating = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _initialComment = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "用户刚看完后最先想说的一句话；自由表达权重最高" };
    private readonly TextBox _subtitlePath = new() { Dock = DockStyle.Fill, ReadOnly = true, PlaceholderText = "可选；SRT / ASS / SSA" };
    private readonly ComboBox _writingStyle = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

    private readonly Button _chooseSubtitle = new() { Text = "选择字幕", AutoSize = true };
    private readonly Button _clearSubtitle = new() { Text = "清除字幕", AutoSize = true };
    private readonly Button _start = new() { Text = "开始 / 重跑第1轮", AutoSize = true };
    private readonly Button _nextRound = new() { Text = "提交本轮 → 第2轮", AutoSize = true, Enabled = false };
    private readonly Button _generateReview = new() { Text = "生成最终短评", AutoSize = true, Enabled = false };
    private readonly Button _editInterviewPrompt = new() { Text = "编辑采访 Prompt", AutoSize = true };
    private readonly Button _editReviewPrompt = new() { Text = "编辑短评 Prompt", AutoSize = true };
    private readonly Button _saveCase = new() { Text = "保存测试案例", AutoSize = true };
    private readonly Button _loadCase = new() { Text = "载入测试案例", AutoSize = true };
    private readonly Button _viewCleanSubtitle = new() { Text = "查看清洗字幕", AutoSize = true };

    private readonly Label _roundStatus = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold), Text = "尚未开始" };
    private readonly Label _subtitleStatus = new() { AutoSize = true, Text = "字幕：未提供" };
    private readonly Label _entityStatus = new() { AutoSize = true, MaximumSize = new Size(820, 0), Text = "实体：尚未建立" };
    private readonly FlowLayoutPanel _questions = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly TextBox _finalFreeText = new() { Multiline = true, Width = 800, Height = 82, ScrollBars = ScrollBars.Vertical, PlaceholderText = "可空；这里完全自由表达，权重最高" };
    private readonly RichTextBox _finalReview = new() { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Microsoft YaHei UI", 11f), Text = "三轮完成后在这里生成短评。" };
    private readonly RichTextBox _metrics = new() { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9f) };
    private readonly ComboBox _callSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Button _viewRequest = new() { Text = "查看 Request", AutoSize = true };
    private readonly Button _viewResponse = new() { Text = "查看 Response", AutoSize = true };
    private readonly Button _viewContent = new() { Text = "查看模型 Content", AutoSize = true };

    private readonly Dictionary<string, QuestionAnswerControls> _answerControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AiCallRecord> _callRecords = [];
    private InterviewSession? _session;
    private InterviewRound? _currentRound;
    private SubtitleCleanResult? _subtitle;
    private string _interviewPrompt = string.Empty;
    private string _reviewPrompt = string.Empty;
    private bool _loadingProvider;
    private bool _finalStageReady;
    private CancellationTokenSource? _operationCts;

    public MainForm()
    {
        _client = new OpenAiCompatibleClient(_httpClient);
        _interviewEngine = new InterviewEngine(_client);
        _reviewEngine = new ReviewEngine(_client);

        Text = "AI 观影短评实验台 v0.1-preview.1｜多模型 · 三轮采访 · Prompt Lab";
        Width = 1580;
        Height = 980;
        MinimumSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;

        _provider.Items.AddRange(ProviderCatalog.Presets.Cast<object>().ToArray());
        _rating.Items.AddRange(["★ 1星", "★★ 2星", "★★★ 3星", "★★★★ 4星", "★★★★★ 5星"]);
        _rating.SelectedIndex = 4;
        _writingStyle.Items.AddRange(["自然随手", "简洁克制", "情绪化", "文艺一点", "直白锐利"]);
        _writingStyle.SelectedIndex = 0;
        _interviewPrompt = _interviewPromptStore.LoadActive();
        _reviewPrompt = _reviewPromptStore.LoadActive();

        BuildUi();
        WireEvents();
        LoadSettings();
        FormClosing += (_, _) => SaveSettings();
    }

    private void BuildUi()
    {
        var root = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 920 };
        Controls.Add(root);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 410));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Panel1.Controls.Add(left);

        left.Controls.Add(BuildInputPanel(), 0, 0);

        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 4, 8, 2) };
        status.Controls.Add(_roundStatus);
        status.Controls.Add(_subtitleStatus);
        status.Controls.Add(_entityStatus);
        left.Controls.Add(status, 0, 1);

        var qGroup = new GroupBox { Text = "当前采访轮次｜每题 A/B/C 可多选；程序固定提供 D.都不符合 + 自由补充", Dock = DockStyle.Fill, Padding = new Padding(8) };
        qGroup.Controls.Add(_questions);
        left.Controls.Add(qGroup, 0, 2);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 265));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 255));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Panel2.Controls.Add(right);

        var reviewGroup = new GroupBox { Text = "最终短评（≤330字）", Dock = DockStyle.Fill, Padding = new Padding(8) };
        reviewGroup.Controls.Add(_finalReview);
        right.Controls.Add(reviewGroup, 0, 0);

        var metricsGroup = new GroupBox { Text = "API 调用指标 / 估算费用", Dock = DockStyle.Fill, Padding = new Padding(8) };
        metricsGroup.Controls.Add(_metrics);
        right.Controls.Add(metricsGroup, 0, 1);

        var rawGroup = new GroupBox { Text = "原始调试数据", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var rawPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        rawPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        rawPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var rawButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        rawButtons.Controls.AddRange([_callSelector, _viewRequest, _viewResponse, _viewContent]);
        rawPanel.Controls.Add(rawButtons, 0, 0);
        rawPanel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "选择一次调用后可查看程序实际发送的 JSON、SSE 原始响应和模型最终 content。API Key 不会写入这些调试数据。",
            AutoSize = false,
            Padding = new Padding(4)
        }, 0, 1);
        rawGroup.Controls.Add(rawPanel);
        right.Controls.Add(rawGroup, 0, 2);
    }

    private Control BuildInputPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(8), AutoSize = false };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        for (var i = 0; i < 9; i++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 6 ? 72 : i == 8 ? 66 : 36));

        AddLabeled(panel, 0, 0, "Provider", _provider, 1);
        AddLabeled(panel, 0, 2, "Model", _model, 3);
        AddLabeled(panel, 1, 0, "Base URL", _baseUrl, 1, span: 3);
        AddLabeled(panel, 2, 0, "API Key", _apiKey, 1, span: 3);

        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        flags.Controls.AddRange([_webSearch, _forceSearch, _thinking]);
        AddLabeled(panel, 3, 0, "模型能力", flags, 1, span: 3);

        var prices = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        prices.Controls.AddRange([
            new Label { Text = "输入/M", AutoSize = true, Padding = new Padding(0, 7, 2, 0) }, _inputPrice,
            new Label { Text = "输出/M", AutoSize = true, Padding = new Padding(8, 7, 2, 0) }, _outputPrice,
            new Label { Text = "缓存/M", AutoSize = true, Padding = new Padding(8, 7, 2, 0) }, _cachePrice
        ]);
        AddLabeled(panel, 4, 0, "价格(元)", prices, 1, span: 3);

        AddLabeled(panel, 5, 0, "电影名称", _movieTitle, 1);
        AddLabeled(panel, 5, 2, "评分", _rating, 3);
        AddLabeled(panel, 6, 0, "初始评论", _initialComment, 1, span: 3);

        var subtitleButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        subtitleButtons.Controls.AddRange([_chooseSubtitle, _clearSubtitle, _viewCleanSubtitle]);
        AddLabeled(panel, 7, 0, "字幕", _subtitlePath, 1);
        panel.Controls.Add(subtitleButtons, 2, 7);
        panel.SetColumnSpan(subtitleButtons, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true };
        actions.Controls.AddRange([
            _start, _nextRound, _generateReview,
            _editInterviewPrompt, _editReviewPrompt,
            _saveCase, _loadCase,
            new Label { Text = "短评文风", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }, _writingStyle
        ]);
        panel.Controls.Add(new Label { Text = "操作", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 8);
        panel.Controls.Add(actions, 1, 8);
        panel.SetColumnSpan(actions, 3);
        return panel;
    }

    private static void AddLabeled(TableLayoutPanel panel, int row, int labelCol, string label, Control control, int controlCol, int span = 1)
    {
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, labelCol, row);
        panel.Controls.Add(control, controlCol, row);
        if (span > 1) panel.SetColumnSpan(control, span);
    }

    private void WireEvents()
    {
        _provider.SelectedIndexChanged += (_, _) => { if (!_loadingProvider) ApplyProviderPreset(); };
        _webSearch.CheckedChanged += (_, _) => _forceSearch.Enabled = _webSearch.Checked && (_webSearch.Enabled || CurrentPreset().Kind == ProviderKind.Custom);
        _chooseSubtitle.Click += async (_, _) => await ChooseSubtitleAsync();
        _clearSubtitle.Click += (_, _) => ClearSubtitle();
        _viewCleanSubtitle.Click += (_, _) => ShowDebug("清洗字幕", _subtitle?.CleanText ?? "尚未载入字幕。 ");
        _start.Click += async (_, _) => await StartAsync();
        _nextRound.Click += async (_, _) => await SubmitCurrentRoundAsync();
        _generateReview.Click += async (_, _) => await GenerateReviewAsync();
        _editInterviewPrompt.Click += (_, _) => EditInterviewPrompt();
        _editReviewPrompt.Click += (_, _) => EditReviewPrompt();
        _saveCase.Click += (_, _) => SaveCase();
        _loadCase.Click += async (_, _) => await LoadCaseAsync();
        _viewRequest.Click += (_, _) => ViewSelectedCall(x => x.RequestJson, "Request JSON");
        _viewResponse.Click += (_, _) => ViewSelectedCall(x => x.RawResponse, "Raw SSE Response");
        _viewContent.Click += (_, _) => ViewSelectedCall(x => x.Content, "模型 Content");
    }

    private void LoadSettings()
    {
        var s = _settingsStore.Load();
        _loadingProvider = true;
        var preset = ProviderCatalog.Find(s.ProviderName);
        _provider.SelectedItem = ProviderCatalog.Presets.FirstOrDefault(x => x.Name == preset.Name) ?? ProviderCatalog.Presets[0];
        _baseUrl.Text = string.IsNullOrWhiteSpace(s.BaseUrl) ? preset.BaseUrl : s.BaseUrl;
        _model.Text = string.IsNullOrWhiteSpace(s.Model) ? preset.Model : s.Model;
        _webSearch.Checked = s.WebSearch;
        _forceSearch.Checked = s.ForceSearchWhenNoSubtitle;
        _thinking.Checked = s.Thinking;
        _inputPrice.Value = ClampPrice(s.InputPricePerMillion);
        _outputPrice.Value = ClampPrice(s.OutputPricePerMillion);
        _cachePrice.Value = ClampPrice(s.CachedInputPricePerMillion);
        _loadingProvider = false;
        UpdateProviderCapabilities();
    }

    private void SaveSettings()
    {
        var selected = CurrentPreset();
        _settingsStore.Save(new LabSettings
        {
            ProviderName = selected.Name,
            BaseUrl = _baseUrl.Text.Trim(),
            Model = _model.Text.Trim(),
            WebSearch = _webSearch.Checked,
            ForceSearchWhenNoSubtitle = _forceSearch.Checked,
            Thinking = _thinking.Checked,
            InputPricePerMillion = _inputPrice.Value,
            OutputPricePerMillion = _outputPrice.Value,
            CachedInputPricePerMillion = _cachePrice.Value
        });
    }

    private void ApplyProviderPreset()
    {
        var p = CurrentPreset();
        _baseUrl.Text = p.BaseUrl;
        _model.Text = p.Model;
        _webSearch.Checked = p.SupportsWebSearch;
        _thinking.Checked = false;
        _inputPrice.Value = ClampPrice(p.InputPricePerMillion);
        _outputPrice.Value = ClampPrice(p.OutputPricePerMillion);
        _cachePrice.Value = ClampPrice(p.CachedInputPricePerMillion);
        UpdateProviderCapabilities();
    }

    private void UpdateProviderCapabilities()
    {
        var p = CurrentPreset();
        _webSearch.Enabled = p.SupportsWebSearch || p.Kind == ProviderKind.Custom;
        if (!_webSearch.Enabled) _webSearch.Checked = false;
        _forceSearch.Enabled = _webSearch.Enabled && _webSearch.Checked;
        _thinking.Enabled = p.SupportsThinking || p.Kind == ProviderKind.Custom;
    }

    private ProviderProfile CurrentProvider()
    {
        var preset = CurrentPreset();
        return new ProviderProfile
        {
            Name = preset.Name,
            Kind = preset.Kind,
            BaseUrl = _baseUrl.Text.Trim(),
            Model = _model.Text.Trim(),
            SupportsWebSearch = preset.Kind == ProviderKind.Custom ? _webSearch.Checked : preset.SupportsWebSearch,
            SupportsThinking = preset.Kind == ProviderKind.Custom ? _thinking.Checked : preset.SupportsThinking,
            InputPricePerMillion = _inputPrice.Value,
            OutputPricePerMillion = _outputPrice.Value,
            CachedInputPricePerMillion = _cachePrice.Value
        };
    }

    private ProviderProfile CurrentPreset() => _provider.SelectedItem as ProviderProfile ?? ProviderCatalog.Presets[0];

}
