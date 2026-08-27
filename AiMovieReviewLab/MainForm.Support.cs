using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        if (string.IsNullOrWhiteSpace(_movieTitle.Text))
        {
            MessageBox.Show(this, "请输入电影名称。", "缺少电影名称");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_apiKey.Text))
        {
            MessageBox.Show(this, "请输入 API Key。Key 不会保存到磁盘。", "缺少 API Key");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_baseUrl.Text) || string.IsNullOrWhiteSpace(_model.Text))
        {
            MessageBox.Show(this, "Base URL 和 Model 不能为空。", "模型配置不完整");
            return false;
        }
        return true;
    }

    private void EditInterviewPrompt()
    {
        using var editor = new PromptEditorForm(
            "编辑三轮采访 System Prompt",
            "当前电影资料、字幕、上一轮回答由程序自动放进 user message。支持占位符 {{ROUND}}、{{ROUND_GUIDANCE}}、{{OUTPUT_SCHEMA}}。每次保存旧版本会自动备份到 LocalAppData 的 History。",
            _interviewPromptStore, _interviewPrompt, "interview");
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _interviewPrompt = _interviewPromptStore.LoadActive();
    }

    private void EditReviewPrompt()
    {
        using var editor = new PromptEditorForm(
            "编辑最终短评 System Prompt",
            "三轮所有回答、自由补充、评分、事实与可选字幕由程序自动送入。支持占位符 {{WRITING_STYLE}}、{{OUTPUT_SCHEMA}}。用户自由文字必须保持最高权重。",
            _reviewPromptStore, _reviewPrompt, "review");
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        _reviewPrompt = _reviewPromptStore.LoadActive();
    }

    private void SaveCase()
    {
        using var dialog = new SaveFileDialog { Filter = "AI短评测试案例|*.json|所有文件|*.*", FileName = SanitizeFileName(_movieTitle.Text) + "_case.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var data = new SavedTestCase
        {
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
        var line = $"{record.Label,-8} | {m.Model,-22} | in {m.PromptTokens,6:N0} | cache {m.CachedPromptTokens,6:N0} | out {m.CompletionTokens,5:N0} | reasoning {m.ReasoningTokens,5:N0} | first {m.FirstTokenMs,5}ms | total {m.TotalElapsedMs,6}ms | web {(m.WebSearchRequested ? "Y" : "N")} | ¥{m.EstimatedCostCny:F6}";
        _metrics.AppendText(line + Environment.NewLine);
        var total = _callRecords.Sum(x => x.Metrics.EstimatedCostCny);
        var tokens = _callRecords.Sum(x => x.Metrics.TotalTokens);
        _metrics.AppendText($"累计：{tokens:N0} tokens｜估算 ¥{total:F6}\r\n\r\n");
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
            _roundStatus.Text = "操作失败，可查看原始数据/调整 Prompt 后重试";
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
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private static string BuildEntityStatus(IEnumerable<EntityAlias> entities)
    {
        var items = entities.Where(x => !string.IsNullOrWhiteSpace(x.Canonical))
            .Select(x => x.Aliases.Count == 0 ? x.Canonical : $"{x.Canonical} ← {string.Join("/", x.Aliases)}")
            .Take(12)
            .ToList();
        return items.Count == 0 ? "实体：尚未建立 / 无需归一" : "实体：" + string.Join("；", items);
    }

    private void ShowDebug(string title, string text) => new DebugTextForm(title, text).ShowDialog(this);

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
