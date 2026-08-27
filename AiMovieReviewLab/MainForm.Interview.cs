using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiMovieReviewLab.Core;

namespace AiMovieReviewLab;

public sealed partial class MainForm
{
    private async Task ChooseSubtitleAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "字幕文件|*.srt;*.ass;*.ssa|SRT|*.srt|ASS/SSA|*.ass;*.ssa|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunBusyAsync(async token =>
        {
            _subtitle = await _subtitleCleaner.CleanAsync(dialog.FileName, token);
            _subtitlePath.Text = dialog.FileName;
            _subtitleStatus.Text = $"字幕：{_subtitle.CleanCharacters:N0} chars / {_subtitle.KeptLines:N0} lines / {_subtitle.EncodingName} / {_subtitle.ElapsedMs}ms";
        });
    }

    private void ClearSubtitle()
    {
        _subtitle = null;
        _subtitlePath.Clear();
        _subtitleStatus.Text = "字幕：未提供";
    }

    private async Task StartAsync()
    {
        if (!ValidateInputs()) return;
        SaveSettings();
        await RunBusyAsync(async token =>
        {
            if (!string.IsNullOrWhiteSpace(_subtitlePath.Text) && (_subtitle is null || !_subtitle.FilePath.Equals(_subtitlePath.Text, StringComparison.OrdinalIgnoreCase)))
                _subtitle = await _subtitleCleaner.CleanAsync(_subtitlePath.Text, token);

            _session = new InterviewSession
            {
                MovieTitle = _movieTitle.Text.Trim(),
                Rating = _rating.SelectedIndex + 1,
                InitialComment = _initialComment.Text.Trim(),
                SubtitleText = _subtitle?.CleanText ?? string.Empty
            };
            _currentRound = null;
            _callRecords.Clear();
            _callSelector.Items.Clear();
            _metrics.Clear();
            _finalReview.Text = "三轮完成后在这里生成短评。";
            _generateReview.Enabled = false;
            _nextRound.Enabled = false;
            _finalFreeText.Clear();
            _finalStageReady = false;
            await GenerateRoundAsync(1, token);
        });
    }

    private async Task SubmitCurrentRoundAsync()
    {
        if (_session is null || _currentRound is null) return;
        var answers = CollectCurrentAnswers(out var unanswered);
        if (unanswered.Count > 0)
        {
            var proceed = MessageBox.Show(this,
                $"还有 {unanswered.Count} 道题没有选择或补充（{string.Join(", ", unanswered)}）。\n允许跳过，继续吗？",
                "存在未回答题目", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!proceed) return;
        }

        foreach (var answer in answers)
        {
            _session.Answers.RemoveAll(x => x.Round == answer.Round && x.QuestionId.Equals(answer.QuestionId, StringComparison.OrdinalIgnoreCase));
            _session.Answers.Add(answer);
        }

        if (_currentRound.Round < 3)
        {
            var next = _currentRound.Round + 1;
            await RunBusyAsync(token => GenerateRoundAsync(next, token));
            return;
        }

        RenderFinalFreeQuestion();
        _finalStageReady = true;
        _roundStatus.Text = "三轮已完成｜最后把话筒交给用户";
        _nextRound.Enabled = false;
        _generateReview.Enabled = true;
    }

    private async Task GenerateRoundAsync(int roundNumber, CancellationToken token)
    {
        if (_session is null) return;
        _roundStatus.Text = $"第 {roundNumber}/3 轮生成中…";
        var provider = CurrentProvider();
        var result = await _interviewEngine.GenerateRoundAsync(
            _session, roundNumber, _interviewPrompt, provider, _apiKey.Text,
            _thinking.Checked, _webSearch.Checked, _forceSearch.Checked, token);
        _currentRound = result.Round;
        _session.Rounds.Add(result.Round);
        AddCallRecord($"第{roundNumber}轮采访", result.Call);
        RenderRound(result.Round);
        _roundStatus.Text = $"第 {roundNumber}/3 轮｜{result.Round.Strategy}";
        _entityStatus.Text = BuildEntityStatus(_session.Entities);
        _nextRound.Enabled = true;
        _nextRound.Text = roundNumber switch
        {
            1 => "提交本轮 → 第2轮",
            2 => "提交本轮 → 第3轮",
            _ => "完成第三轮"
        };
    }

    private void RenderRound(InterviewRound round)
    {
        _questions.SuspendLayout();
        _questions.Controls.Clear();
        _answerControls.Clear();

        foreach (var q in round.Questions)
        {
            var box = new GroupBox
            {
                Width = 850,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Text = $"{q.Id}｜{q.Purpose}｜{q.Topic}",
                Padding = new Padding(10),
                Margin = new Padding(4, 4, 4, 12)
            };
            var panel = new FlowLayoutPanel
            {
                Width = 820,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(800, 0),
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                Text = q.Question,
                Margin = new Padding(3, 3, 3, 7)
            });

            var controls = new QuestionAnswerControls(q);
            for (var i = 0; i < q.Options.Count; i++)
            {
                var label = $"{(char)('A' + i)}. {q.Options[i]}";
                var cb = new CheckBox { AutoSize = true, MaximumSize = new Size(790, 0), Text = label, Margin = new Padding(16, 2, 3, 2) };
                controls.Options.Add((q.Options[i], cb));
                panel.Controls.Add(cb);
            }
            var none = new CheckBox { AutoSize = true, Text = "D. 都不符合", Margin = new Padding(16, 2, 3, 4) };
            controls.NoneCheck = none;
            panel.Controls.Add(none);

            foreach (var item in controls.Options)
                item.Control.CheckedChanged += (_, _) => { if (item.Control.Checked) none.Checked = false; };
            none.CheckedChanged += (_, _) =>
            {
                if (!none.Checked) return;
                foreach (var item in controls.Options) item.Control.Checked = false;
            };

            panel.Controls.Add(new Label { AutoSize = true, Text = "补充你的想法（可选，权重高于选项）：", Margin = new Padding(3, 6, 3, 2) });
            var free = new TextBox { Multiline = true, Width = 790, Height = 58, ScrollBars = ScrollBars.Vertical };
            controls.FreeText = free;
            panel.Controls.Add(free);

            box.Controls.Add(panel);
            _questions.Controls.Add(box);
            _answerControls[q.Id] = controls;
        }
        _questions.ResumeLayout();
    }

    private void RenderFinalFreeQuestion()
    {
        _questions.SuspendLayout();
        _questions.Controls.Clear();
        var box = new GroupBox
        {
            Width = 850,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = "最后自由发言｜可选｜最高权重",
            Padding = new Padding(12)
        };
        var panel = new FlowLayoutPanel { Width = 820, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(800, 0),
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            Text = "还有没有什么刚才没问到，但你特别想说的？"
        });
        panel.Controls.Add(_finalFreeText);
        box.Controls.Add(panel);
        _questions.Controls.Add(box);
        _questions.ResumeLayout();
    }

    private List<QuestionAnswer> CollectCurrentAnswers(out List<string> unanswered)
    {
        var answers = new List<QuestionAnswer>();
        unanswered = [];
        if (_currentRound is null) return answers;

        foreach (var q in _currentRound.Questions)
        {
            if (!_answerControls.TryGetValue(q.Id, out var controls))
            {
                unanswered.Add(q.Id);
                continue;
            }
            var selected = controls.Options.Where(x => x.Control.Checked).Select(x => x.Text).ToList();
            var free = controls.FreeText.Text.Trim();
            if (controls.NoneCheck.Checked) selected.Add("都不符合");
            if (selected.Count == 0 && string.IsNullOrWhiteSpace(free))
            {
                unanswered.Add(q.Id);
                continue;
            }
            answers.Add(new QuestionAnswer
            {
                Round = _currentRound.Round,
                QuestionId = q.Id,
                Question = q.Question,
                SelectedOptions = selected,
                FreeText = free
            });
        }
        return answers;
    }

}
