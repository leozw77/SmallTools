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
        if (!TryNormalizeDoubanUrl(_doubanUrl.Text, out var canonicalUrl, out var subjectId)) return;
        _doubanUrl.Text = canonicalUrl;
        SaveSettings();

        await RunBusyAsync(async token =>
        {
            if (!string.IsNullOrWhiteSpace(_subtitlePath.Text) && (_subtitle is null || !_subtitle.FilePath.Equals(_subtitlePath.Text, StringComparison.OrdinalIgnoreCase)))
                _subtitle = await _subtitleCleaner.CleanAsync(_subtitlePath.Text, token);

            _movieTitle.Clear();
            _session = new InterviewSession
            {
                DoubanUrl = canonicalUrl,
                DoubanSubjectId = subjectId,
                MovieTitle = string.Empty,
                Rating = _rating.SelectedIndex + 1,
                InitialComment = _initialComment.Text.Trim(),
                SubtitleText = _subtitle?.CleanText ?? string.Empty
            };
            _currentRound = null;
            _callRecords.Clear();
            _callSelector.Items.Clear();
            _metrics.Clear();
            _finalReview.Text = "三轮完成后在这里生成短评。";
            _factStatus.Text = $"准备事实定位…\r\n豆瓣：{canonicalUrl}\r\nSubject：{subjectId}\r\n第一轮会读取指定链接，必要时做一次精确搜索，同时建立第二轮发散候选。";
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

        _session.FinalFreeText = _finalFreeText.Text.Trim();
        _finalStageReady = true;
        _roundStatus.Text = "三轮已完成｜9个问题 + 同页自由题已提交，可直接生成短评";
        _nextRound.Enabled = false;
        _generateReview.Enabled = true;
    }

    private async Task GenerateRoundAsync(int roundNumber, CancellationToken token)
    {
        if (_session is null) return;
        _roundStatus.Text = roundNumber == 1 ? "第 1/3 轮｜正在读取豆瓣并定位用户所指内容…" : $"第 {roundNumber}/3 轮生成中…";
        var provider = CurrentProvider();
        var result = await _interviewEngine.GenerateRoundAsync(
            _session, roundNumber, _interviewPrompt, provider, _apiKey.Text,
            _thinking.Checked, _webSearch.Checked, token);

        _currentRound = result.Round;
        _session.Rounds.RemoveAll(x => x.Round == roundNumber);
        _session.Rounds.Add(result.Round);
        AddCallRecord($"第{roundNumber}轮采访", result.Call);

        if (roundNumber == 1)
        {
            _movieTitle.Text = _session.MovieTitle;
            _factStatus.Text = BuildFactStatus(result.Round.FactLocalization, result.Call, provider);
        }

        RenderRound(result.Round);
        _roundStatus.Text = $"第 {roundNumber}/3 轮｜{result.Round.Strategy}";
        _entityStatus.Text = BuildEntityStatus(_session.Entities);
        _nextRound.Enabled = true;
        _nextRound.Text = roundNumber switch
        {
            1 => "提交本轮 → 第2轮",
            2 => "提交本轮 → 第3轮",
            _ => "完成采访（含自由题）"
        };
    }

    private void RenderRound(InterviewRound round)
    {
        _questions.SuspendLayout();
        _questions.Controls.Clear();
        _answerControls.Clear();

        var cardWidth = Math.Max(560, _questions.ClientSize.Width - 42);
        foreach (var q in round.Questions)
        {
            var typeLabel = q.QuestionType.Equals("discovery", StringComparison.OrdinalIgnoreCase) ? "｜发散题·可多选" : string.Empty;
            var box = new GroupBox
            {
                Width = cardWidth,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Text = $"{q.Id}｜{q.Purpose}｜{q.Topic}{typeLabel}",
                Padding = new Padding(10),
                Margin = new Padding(4, 4, 4, 12),
                Tag = "question-card"
            };
            var panel = new FlowLayoutPanel
            {
                Width = Math.Max(520, cardWidth - 26),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Tag = "question-panel"
            };
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(Math.Max(480, cardWidth - 60), 0),
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                Text = q.Question,
                Margin = new Padding(3, 3, 3, 7)
            });

            var controls = new QuestionAnswerControls(q);
            for (var i = 0; i < q.Options.Count; i++)
            {
                var label = $"{(char)('A' + i)}. {q.Options[i]}";
                var cb = new CheckBox { AutoSize = true, MaximumSize = new Size(Math.Max(460, cardWidth - 80), 0), Text = label, Margin = new Padding(16, 2, 3, 2) };
                controls.Options.Add((q.Options[i], cb));
                panel.Controls.Add(cb);
            }
            var none = new CheckBox { AutoSize = true, Text = "都不符合", Margin = new Padding(16, 4, 3, 4) };
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
            var free = new TextBox { Multiline = true, Width = Math.Max(480, cardWidth - 70), Height = 58, ScrollBars = ScrollBars.Vertical };
            controls.FreeText = free;
            panel.Controls.Add(free);

            box.Controls.Add(panel);
            _questions.Controls.Add(box);
            _answerControls[q.Id] = controls;
        }

        if (round.Round == 3)
            AppendFinalFreeQuestionCard(cardWidth);

        _questions.ResumeLayout();
    }

    private void ResizeQuestionCards()
    {
        var cardWidth = Math.Max(560, _questions.ClientSize.Width - 42);
        foreach (Control control in _questions.Controls)
        {
            if (control is not GroupBox box || !Equals(box.Tag, "question-card")) continue;
            box.Width = cardWidth;
            foreach (Control child in box.Controls)
            {
                if (child is FlowLayoutPanel panel && Equals(panel.Tag, "question-panel"))
                    panel.Width = Math.Max(520, cardWidth - 26);
            }
        }
    }

    private void AppendFinalFreeQuestionCard(int cardWidth)
    {
        var box = new GroupBox
        {
            Width = cardWidth,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = "固定自由题｜可选｜最高权重",
            Padding = new Padding(12),
            Margin = new Padding(4, 4, 4, 16),
            Tag = "question-card"
        };
        var panel = new FlowLayoutPanel
        {
            Width = Math.Max(520, cardWidth - 26),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Tag = "question-panel"
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(Math.Max(480, cardWidth - 60), 0),
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            Text = "还有没有什么刚才没问到，但你特别想说的？"
        });
        _finalFreeText.Width = Math.Max(480, cardWidth - 70);
        panel.Controls.Add(_finalFreeText);
        box.Controls.Add(panel);
        _questions.Controls.Add(box);
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
                QuestionType = q.QuestionType,
                Question = q.Question,
                SelectedOptions = selected,
                FreeText = free
            });
        }
        return answers;
    }
}
