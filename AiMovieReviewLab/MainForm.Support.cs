using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiMovieReviewLab.Core;

namespace AiMovieReviewLab;

public sealed partial class MainForm
{
    private async Task GenerateReviewAsync()
    {
        if (_session is null) return;
        _session.FinalFreeText = _finalFreeText.Text.Trim();
        await RunBusyAsync(async token =>
        {
            var provider = CurrentProvider();
            var style = _writingStyle.SelectedItem?.ToString() ?? "自然随手";
            _finalReview.Text = "正在整理短评…";
            var result = await _reviewEngine.GenerateAsync(
                _session, _reviewPrompt, style, provider, _apiKey.Text, _thinking.Checked, token);
            _finalReview.Text = result.Output.Review;
            AddCallRecord("最终短评", result.Call);
        });
    }

    private bool ValidateInputs()
    {
        if (!TryNormalizeDoubanUrl(_doubanUrl.Text, out _, out _)) return false;
        if (string.IsNullOrWhiteSpace(_initialComment.Text))
        {
            MessageBox.Show(this, "AI补充模式需要至少一句观影观点作为采访锚点，例如“好看”“结尾让我很难受”“某个角色很喜欢”。", "缺少初始评论");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_apiKey.Text))
        {
            MessageBox.Show(this, "请输入 API Key。Key 不会保存到磁盘，也不会写入日志。", "缺少 API Key");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_baseUrl.Text) || string.IsNullOrWhiteSpace(_model.Text))
        {
            MessageBox.Show(this, "Base URL 和 Model 不能为空。", "模型配置不完整");
            return false;
        }
        return true;
    }

    private bool TryNormalizeDoubanUrl(string input, out string canonicalUrl, out string subjectId)
    {
        canonicalUrl = string.Empty;
        subjectId = string.Empty;
        var text = (input ?? string.Empty).Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !(uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            || !uri.Host.Equals("movie.douban.com", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "请输入豆瓣电影主体链接，例如：\nhttps://movie.douban.com/subject/1292052/", "豆瓣链接不合法");
            return false;
        }

        var match = Regex.Match(uri.AbsolutePath, @"^/subject/(\d+)(?:/|$)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            MessageBox.Show(this, "只接受豆瓣电影 subject 链接，链接中必须包含 /subject/数字ID/。", "不是豆瓣电影主体页");
            return false;
        }

        subjectId = match.Groups[1].Value;
        canonicalUrl = $"https://movie.douban.com/subject/{subjectId}/";
        return true;
    }

    private void EditInterviewPrompt()
    {
        using var editor = new PromptEditorForm(
            "编辑三轮采访 System Prompt",
            "第一轮由程序同时提供强制豆瓣URL、可选字幕和web_extractor/web_search工具；第二三轮只带已验证事实与用户回答。支持 {{ROUND}}、{{ROUND_GUIDANCE}}、{{OUTPUT_SCHEMA}}。保存旧版会自动进入 History。",
            _interviewPromptStore, _interviewPrompt, "interview");
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _interviewPrompt = _interviewPromptStore.LoadActive();
    }

    private void EditReviewPrompt()
    {
        using var editor = new PromptEditorForm(
            "编辑最终短评 System Prompt",
            "三轮回答、自由补充、评分和第一轮已验证事实会自动送入。支持 {{WRITING_STYLE}}、{{OUTPUT_SCHEMA}}。自由文字始终高于选项。",
            _reviewPromptStore, _reviewPrompt, "review");
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _reviewPrompt = _reviewPromptStore.LoadActive();
    }

    private void SaveCase()
    {
        var name = !string.IsNullOrWhiteSpace(_movieTitle.Text) ? _movieTitle.Text : "douban_" + ExtractSubjectIdForFileName();
        using var dialog = new SaveFileDialog { Filter = "AI短评测试案例|*.json|所有文件|*.*", FileName = SanitizeFileName(name) + "_case.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var data = new SavedTestCase
        {
            DoubanUrl = _doubanUrl.Text.Trim(),
            MovieTitle = _movieTitle.Text.Trim(),
            Rating = _rating.SelectedIndex + 1,
            InitialComment = _initialComment.Text,
            SubtitlePath = _subtitlePath.Text,
            WritingStyle = _writingStyle.SelectedItem?.ToString() ?? "自然随手"
        };
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(data, PrettyJson()));
    }

    private async Task LoadCaseAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "AI短评测试案例|*.json|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var data = JsonSerializer.Deserialize<SavedTestCase>(File.ReadAllText(dialog.FileName), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (data is null) return;
        _doubanUrl.Text = data.DoubanUrl;
        _movieTitle.Text = data.MovieTitle;
        _rating.SelectedIndex = Math.Clamp(data.Rating - 1, 0, 4);
        _initialComment.Text = data.InitialComment;
        if (_writingStyle.Items.Contains(data.WritingStyle)) _writingStyle.SelectedItem = data.WritingStyle;
        if (!string.IsNullOrWhiteSpace(data.SubtitlePath) && File.Exists(data.SubtitlePath))
        {
            await RunBusyAsync(async token =>
            {
                _subtitle = await _subtitleCleaner.CleanAsync(data.SubtitlePath, token);
                _subtitlePath.Text = data.SubtitlePath;
                _subtitleStatus.Text = $"字幕：{_subtitle.CleanCharacters:N0} chars / {_subtitle.KeptLines:N0} lines";
            });
        }
        else
        {
            ClearSubtitle();
        }
    }

    private void AddCallRecord(string label, AiCallResult call)
    {
        var record = new AiCallRecord
        {
            Label = label,
            Time = DateTime.Now,
            RequestJson = call.RequestJson,
            RawResponse = call.RawResponse,
            Content = call.Content,
            ReasoningSummary = call.ReasoningSummary,
            ToolCalls = call.ToolCalls,
            Metrics = call.Metrics
        };
        _callRecords.Add(record);
        _callSelector.Items.Add($"{_callRecords.Count}. {label}");
        _callSelector.SelectedIndex = _callSelector.Items.Count - 1;
        AppendMetric(record);
    }

    private void AppendMetric(AiCallRecord record)
    {
        var m = record.Metrics;
        var first = m.FirstTokenMs > 0 ? $"{m.FirstTokenMs}ms" : "n/a";
        var line = $"{record.Label,-8} | {m.ApiMode,-33} | {m.Model,-20} | in {m.PromptTokens,6:N0} | cache {m.CachedPromptTokens,6:N0} | out {m.CompletionTokens,5:N0} | reasoning {m.ReasoningTokens,5:N0} | first {first,7} | total {m.TotalElapsedMs,6}ms | search {m.WebSearchCount} | extract {m.WebExtractorCount} | ¥{m.EstimatedCostCny:F6}";
        _metrics.AppendText(line + Environment.NewLine);
        var total = _callRecords.Sum(x => x.Metrics.EstimatedCostCny);
        var tokens = _callRecords.Sum(x => x.Metrics.TotalTokens);
        _metrics.AppendText($"累计：{tokens:N0} tokens｜估算 ¥{total:F6}\r\n\r\n");
    }

    private string BuildFactStatus(FactLocalization? fact, AiCallResult call, ProviderProfile provider)
    {
        if (fact is null)
            return provider.SupportsWebExtractor
                ? "第一轮没有返回事实定位对象。"
                : "当前 Provider 不支持强制 web_extractor，第一轮只能退化为供应商联网/提示词定位。";

        var lines = new List<string>
        {
            $"豆瓣读取：{fact.DoubanReadStatus}",
            $"Subject：{fact.SubjectId}",
            $"影片：{fact.MovieTitle}",
            $"API：{call.Metrics.ApiMode}",
            $"工具：web_extractor {call.Metrics.WebExtractorCount} 次；web_search {call.Metrics.WebSearchCount} 次"
        };
        if (!string.IsNullOrWhiteSpace(fact.SceneSummary)) lines.Add($"场景定位：{fact.SceneSummary} [{fact.SceneConfidence}]");
        if (fact.VerifiedEntities.Count > 0)
            lines.Add("已确认实体：" + string.Join("；", fact.VerifiedEntities.Select(x => x.Canonical)));
        if (fact.UncertainEntities.Count > 0)
            lines.Add("未锁定实体：" + string.Join("；", fact.UncertainEntities.Select(x => x.Canonical)));
        if (fact.Unresolved.Count > 0)
            lines.Add("未确认：" + string.Join("；", fact.Unresolved));
        if (fact.Sources.Count > 0)
            lines.Add("来源：" + string.Join(" | ", fact.Sources.Take(4)));
        return string.Join(Environment.NewLine, lines);
    }

    private void ViewSelectedCall(Func<AiCallRecord, string> selector, string title)
    {
        var index = _callSelector.SelectedIndex;
        if (index < 0 || index >= _callRecords.Count) return;
        ShowDebug(title + "｜" + _callRecords[index].Label, selector(_callRecords[index]));
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await work(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            _roundStatus.Text = "操作已取消";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _roundStatus.Text = "操作失败；可导出完整日志或查看原始数据后继续调整";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _start.Enabled = !busy;
        _nextRound.Enabled = !busy && _currentRound is not null && !_finalStageReady;
        _generateReview.Enabled = !busy && _session is not null && _finalStageReady;
        _chooseSubtitle.Enabled = !busy;
        _clearSubtitle.Enabled = !busy;
        _editInterviewPrompt.Enabled = !busy;
        _editReviewPrompt.Enabled = !busy;
        _provider.Enabled = !busy;
        _thinking.Enabled = !busy && (CurrentPreset().SupportsThinking || CurrentPreset().Kind == ProviderKind.Custom);
        _exportLog.Enabled = !busy;
        _copyLog.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private static string BuildEntityStatus(IEnumerable<EntityAlias> entities)
    {
        var items = entities.Where(x => !string.IsNullOrWhiteSpace(x.Canonical))
            .Select(x => x.Aliases.Count == 0 ? $"✓ {x.Canonical}" : $"✓ {x.Canonical} ← {string.Join("/", x.Aliases)}")
            .Take(12)
            .ToList();
        return items.Count == 0 ? "已验证实体：尚未建立 / 没有可安全锁定的实体" : "已验证实体：" + string.Join("；", items);
    }

    private string ExtractSubjectIdForFileName()
    {
        return TryNormalizeDoubanUrlNoDialog(_doubanUrl.Text, out _, out var id) ? id : "unknown";
    }

    private static bool TryNormalizeDoubanUrlNoDialog(string input, out string canonicalUrl, out string subjectId)
    {
        canonicalUrl = string.Empty;
        subjectId = string.Empty;
        if (!Uri.TryCreate((input ?? string.Empty).Trim(), UriKind.Absolute, out var uri) || !uri.Host.Equals("movie.douban.com", StringComparison.OrdinalIgnoreCase)) return false;
        var match = Regex.Match(uri.AbsolutePath, @"^/subject/(\d+)(?:/|$)", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        subjectId = match.Groups[1].Value;
        canonicalUrl = $"https://movie.douban.com/subject/{subjectId}/";
        return true;
    }

    private void ShowDebug(string title, string text) => new DebugTextForm(title, RedactSecrets(text)).ShowDialog(this);

    private string RedactSecrets(string text)
    {
        var result = text ?? string.Empty;
        var key = _apiKey.Text.Trim();
        if (!string.IsNullOrWhiteSpace(key)) result = result.Replace(key, "[REDACTED_API_KEY]", StringComparison.Ordinal);
        result = Regex.Replace(result, @"(?i)(Authorization\s*[:=]\s*Bearer\s+)[^\s\"']+", "$1[REDACTED]");
        return result;
    }

    private static NumericUpDown PriceBox() => new()
    {
        Width = 78,
        DecimalPlaces = 3,
        Increment = 0.01m,
        Minimum = 0,
        Maximum = 1000,
        ThousandsSeparator = true
    };

    private static decimal ClampPrice(decimal value) => Math.Clamp(value, 0m, 1000m);

    private static JsonSerializerOptions PrettyJson() => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string SanitizeFileName(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "movie" : value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
        return text;
    }

    private sealed class QuestionAnswerControls(InterviewQuestion question)
    {
        public InterviewQuestion Question { get; } = question;
        public List<(string Text, CheckBox Control)> Options { get; } = [];
        public CheckBox NoneCheck { get; set; } = null!;
        public TextBox FreeText { get; set; } = null!;
    }
}
