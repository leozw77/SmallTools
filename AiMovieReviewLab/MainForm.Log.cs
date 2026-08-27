using System.Text;
using AiMovieReviewLab.Core;

namespace AiMovieReviewLab;

public sealed partial class MainForm
{
    private void ExportFullLogToDesktop()
    {
        try
        {
            var markdown = BuildFullLogMarkdown();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Directory.CreateDirectory(desktop);
            var nameBase = !string.IsNullOrWhiteSpace(_movieTitle.Text)
                ? _movieTitle.Text.Trim()
                : "Douban-" + ExtractSubjectIdForFileName();
            var fileName = $"AI观影短评实验台_{SanitizeFileName(nameBase)}_{DateTime.Now:yyyyMMdd-HHmmss}.md";
            var path = Path.Combine(desktop, fileName);
            File.WriteAllText(path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            MessageBox.Show(this, $"完整日志已直接保存到桌面：\n{path}", "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出日志失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopyFullLog()
    {
        try
        {
            var markdown = BuildFullLogMarkdown();
            Clipboard.SetText(markdown);
            MessageBox.Show(this, "完整日志已经复制到剪贴板，可以直接粘贴给 ChatGPT。", "复制成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "复制日志失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string BuildFullLogMarkdown()
    {
        var provider = CurrentProvider();
        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("# AI 观影短评实验台完整调试日志");
        sb.AppendLine();
        sb.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 程序版本：v{Application.ProductVersion}");
        sb.AppendLine("- API Key：**未导出 / 已排除**");
        sb.AppendLine();

        sb.AppendLine("## 1. 模型配置");
        sb.AppendLine();
        sb.AppendLine($"- Provider：{provider.Name}");
        sb.AppendLine($"- Base URL：`{_baseUrl.Text.Trim()}`");
        sb.AppendLine($"- Model：`{_model.Text.Trim()}`");
        sb.AppendLine($"- 第一轮强制豆瓣事实定位：是");
        sb.AppendLine($"- 第一轮允许必要时补充联网搜索：{(_webSearch.Checked ? "是" : "否")}");
        sb.AppendLine($"- Thinking：{(_thinking.Checked ? "开" : "关")}");
        sb.AppendLine($"- 输入价格：¥{_inputPrice.Value}/M Token");
        sb.AppendLine($"- 输出价格：¥{_outputPrice.Value}/M Token");
        sb.AppendLine($"- 缓存输入价格：¥{_cachePrice.Value}/M Token");
        sb.AppendLine();

        sb.AppendLine("## 2. 影片与用户输入");
        sb.AppendLine();
        sb.AppendLine($"- 豆瓣链接：{_doubanUrl.Text.Trim()}");
        sb.AppendLine($"- 豆瓣 Subject ID：{(_session?.DoubanSubjectId ?? ExtractSubjectIdForFileName())}");
        sb.AppendLine($"- 识别影片：{ValueOrEmpty(_session?.MovieTitle ?? _movieTitle.Text)}");
        sb.AppendLine($"- 评分：{(_rating.SelectedIndex + 1)}/5");
        sb.AppendLine("- 初始评论：");
        sb.AppendLine();
        QuoteBlock(sb, _initialComment.Text);
        sb.AppendLine();

        sb.AppendLine("## 3. 字幕");
        sb.AppendLine();
        if (_subtitle is null)
        {
            sb.AppendLine("- 未提供字幕。");
        }
        else
        {
            sb.AppendLine($"- 文件：`{_subtitle.FilePath}`");
            sb.AppendLine($"- 编码：{_subtitle.EncodingName}");
            sb.AppendLine($"- 原始字符：{_subtitle.RawCharacters:N0}");
            sb.AppendLine($"- 清洗字符：{_subtitle.CleanCharacters:N0}");
            sb.AppendLine($"- 解析块：{_subtitle.ParsedBlocks:N0}");
            sb.AppendLine($"- 保留行：{_subtitle.KeptLines:N0}");
            sb.AppendLine($"- 清洗耗时：{_subtitle.ElapsedMs}ms");
            sb.AppendLine();
            sb.AppendLine("### 清洗后的完整字幕");
            CodeBlock(sb, _subtitle.CleanText, "text");
        }
        sb.AppendLine();

        sb.AppendLine("## 4. 第一轮事实定位 / AI 判断记录");
        sb.AppendLine();
        var firstFact = _session?.Rounds.FirstOrDefault(x => x.Round == 1)?.FactLocalization;
        if (firstFact is null)
        {
            sb.AppendLine("尚未生成第一轮事实定位。");
        }
        else
        {
            AppendFactLocalization(sb, firstFact);
        }
        sb.AppendLine();

        sb.AppendLine("### 每轮已验证事实快照");
        sb.AppendLine();
        if (_session?.FactSnapshots.Count > 0)
        {
            foreach (var snapshot in _session.FactSnapshots.OrderBy(x => x.Round))
            {
                sb.AppendLine($"#### Round {snapshot.Round}");
                sb.AppendLine($"- 豆瓣读取状态：{snapshot.DoubanReadStatus}");
                sb.AppendLine($"- 场景定位：{ValueOrEmpty(snapshot.SceneSummary)} [{snapshot.SceneConfidence}]");
                sb.AppendLine("- 已验证实体：" + (snapshot.VerifiedEntities.Count == 0 ? "无" : string.Join("；", snapshot.VerifiedEntities.Select(FormatEntity))));
                sb.AppendLine("- 已验证事实：" + (snapshot.VerifiedFacts.Count == 0 ? "无" : string.Join("；", snapshot.VerifiedFacts)));
                sb.AppendLine("- 未确认：" + (snapshot.Unresolved.Count == 0 ? "无" : string.Join("；", snapshot.Unresolved)));
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("尚无快照。");
        }
        sb.AppendLine();

        sb.AppendLine("## 5. 三轮采访：完整问题、全部选项与用户回答");
        sb.AppendLine();
        if (_session?.Rounds.Count > 0)
        {
            foreach (var round in _session.Rounds.OrderBy(x => x.Round))
            {
                sb.AppendLine($"### 第 {round.Round}/3 轮");
                sb.AppendLine();
                sb.AppendLine($"- Strategy：{round.Strategy}");
                sb.AppendLine();
                foreach (var q in round.Questions)
                {
                    sb.AppendLine($"#### {q.Id}｜{q.Purpose}｜{q.Topic}");
                    sb.AppendLine();
                    sb.AppendLine($"**问题：** {q.Question}");
                    sb.AppendLine();
                    for (var i = 0; i < q.Options.Count; i++)
                        sb.AppendLine($"- {(char)('A' + i)}. {q.Options[i]}");
                    sb.AppendLine("- D. 都不符合");
                    sb.AppendLine();

                    var answer = GetAnswerForLog(round.Round, q);
                    sb.AppendLine("**用户勾选：** " + (answer.SelectedOptions.Count == 0 ? "（未选择）" : string.Join("；", answer.SelectedOptions)));
                    sb.AppendLine();
                    sb.AppendLine("**用户自由补充：**");
                    QuoteBlock(sb, answer.FreeText);
                    sb.AppendLine();
                }
            }
        }
        else
        {
            sb.AppendLine("尚未生成采访问题。");
        }

        sb.AppendLine("## 6. 最终自由发言");
        sb.AppendLine();
        QuoteBlock(sb, _finalFreeText.Text);
        sb.AppendLine();

        sb.AppendLine("## 7. 最终短评");
        sb.AppendLine();
        QuoteBlock(sb, _finalReview.Text);
        sb.AppendLine();

        sb.AppendLine("## 8. 当前采访 Prompt（完整）");
        sb.AppendLine();
        CodeBlock(sb, _interviewPrompt, "text");
        sb.AppendLine();

        sb.AppendLine("## 9. 当前最终短评 Prompt（完整）");
        sb.AppendLine();
        CodeBlock(sb, _reviewPrompt, "text");
        sb.AppendLine();

        sb.AppendLine("## 10. API 调用完整记录");
        sb.AppendLine();
        if (_callRecords.Count == 0)
        {
            sb.AppendLine("尚无 API 调用记录。");
        }
        else
        {
            for (var i = 0; i < _callRecords.Count; i++)
            {
                var call = _callRecords[i];
                var m = call.Metrics;
                sb.AppendLine($"### Call {i + 1}｜{call.Label}");
                sb.AppendLine();
                sb.AppendLine($"- 时间：{call.Time:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"- API 模式：{m.ApiMode}");
                sb.AppendLine($"- Model：{m.Model}");
                sb.AppendLine($"- Thinking：{m.ThinkingEnabled}");
                sb.AppendLine($"- 输入 Token：{m.PromptTokens:N0}");
                sb.AppendLine($"- 缓存输入 Token：{m.CachedPromptTokens:N0}");
                sb.AppendLine($"- 输出 Token：{m.CompletionTokens:N0}");
                sb.AppendLine($"- Reasoning Token：{m.ReasoningTokens:N0}");
                sb.AppendLine($"- 总 Token：{m.TotalTokens:N0}");
                sb.AppendLine($"- 首 Token：{(m.FirstTokenMs > 0 ? m.FirstTokenMs + "ms" : "n/a（非流式 Responses）")}");
                sb.AppendLine($"- 总耗时：{m.TotalElapsedMs}ms");
                sb.AppendLine($"- web_search 次数：{m.WebSearchCount}");
                sb.AppendLine($"- web_extractor 次数：{m.WebExtractorCount}");
                sb.AppendLine($"- 估算模型 Token 费用：¥{m.EstimatedCostCny:F6}");
                sb.AppendLine();

                if (call.ToolCalls.Count > 0)
                {
                    sb.AppendLine("#### 工具调用");
                    foreach (var tool in call.ToolCalls)
                    {
                        sb.AppendLine($"- 类型：`{tool.Type}`");
                        if (!string.IsNullOrWhiteSpace(tool.QueryOrGoal)) sb.AppendLine($"  - Query/Goal：{tool.QueryOrGoal}");
                        if (tool.Urls.Count > 0) sb.AppendLine($"  - URL：{string.Join(" | ", tool.Urls)}");
                        if (!string.IsNullOrWhiteSpace(tool.Output))
                        {
                            sb.AppendLine("  - Tool Output：");
                            CodeBlock(sb, RedactSecrets(tool.Output), "text");
                        }
                    }
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(call.ReasoningSummary))
                {
                    sb.AppendLine("#### Provider 返回的 reasoning summary");
                    sb.AppendLine();
                    sb.AppendLine("仅记录供应商显式返回的摘要，不尝试提取或展示隐藏内部思维链。");
                    CodeBlock(sb, RedactSecrets(call.ReasoningSummary), "text");
                    sb.AppendLine();
                }

                sb.AppendLine("#### 模型 Content");
                CodeBlock(sb, RedactSecrets(call.Content), "text");
                sb.AppendLine();
                sb.AppendLine("#### Request JSON");
                CodeBlock(sb, RedactSecrets(call.RequestJson), "json");
                sb.AppendLine();
                sb.AppendLine("#### Raw Response");
                CodeBlock(sb, RedactSecrets(call.RawResponse), "text");
                sb.AppendLine();
            }
        }

        var totalTokens = _callRecords.Sum(x => x.Metrics.TotalTokens);
        var totalCost = _callRecords.Sum(x => x.Metrics.EstimatedCostCny);
        sb.AppendLine("## 11. 汇总");
        sb.AppendLine();
        sb.AppendLine($"- API 调用数：{_callRecords.Count}");
        sb.AppendLine($"- 总 Token：{totalTokens:N0}");
        sb.AppendLine($"- 估算模型 Token 总费用：¥{totalCost:F6}");
        sb.AppendLine($"- web_search 总次数：{_callRecords.Sum(x => x.Metrics.WebSearchCount)}");
        sb.AppendLine($"- web_extractor 总次数：{_callRecords.Sum(x => x.Metrics.WebExtractorCount)}");
        sb.AppendLine();
        sb.AppendLine("> 注：这里的费用只按界面配置的 Token 单价估算；联网搜索/网页抓取若有独立工具费用，应以供应商账单为准。API Key 不在本日志中。 ");

        return RedactSecrets(sb.ToString());
    }

    private QuestionAnswer GetAnswerForLog(int roundNumber, InterviewQuestion q)
    {
        if (_currentRound?.Round == roundNumber && _answerControls.TryGetValue(q.Id, out var controls))
        {
            var selected = controls.Options.Where(x => x.Control.Checked).Select(x => x.Text).ToList();
            if (controls.NoneCheck.Checked) selected.Add("都不符合");
            return new QuestionAnswer
            {
                Round = roundNumber,
                QuestionId = q.Id,
                Question = q.Question,
                SelectedOptions = selected,
                FreeText = controls.FreeText.Text.Trim()
            };
        }

        return _session?.Answers.LastOrDefault(x => x.Round == roundNumber && x.QuestionId.Equals(q.Id, StringComparison.OrdinalIgnoreCase))
               ?? new QuestionAnswer { Round = roundNumber, QuestionId = q.Id, Question = q.Question };
    }

    private static void AppendFactLocalization(StringBuilder sb, FactLocalization fact)
    {
        sb.AppendLine($"- 豆瓣读取状态：**{fact.DoubanReadStatus}**");
        sb.AppendLine($"- Subject ID：{fact.SubjectId}");
        sb.AppendLine($"- 影片：{fact.MovieTitle}");
        sb.AppendLine($"- 影片身份：{ValueOrEmpty(fact.MovieIdentity)}");
        sb.AppendLine($"- 是否需要定位具体场景：{fact.NeedsSceneLocalization}");
        sb.AppendLine($"- 场景定位：{ValueOrEmpty(fact.SceneSummary)}");
        sb.AppendLine($"- 场景置信度：{fact.SceneConfidence}");
        sb.AppendLine($"- 是否使用补充搜索：{fact.FallbackSearchUsed}");
        sb.AppendLine();
        sb.AppendLine("### 已验证实体（只有这些允许锁定）");
        if (fact.VerifiedEntities.Count == 0) sb.AppendLine("- 无");
        foreach (var entity in fact.VerifiedEntities)
        {
            sb.AppendLine($"- {FormatEntity(entity)}");
            sb.AppendLine($"  - Evidence：{ValueOrEmpty(entity.Evidence)}");
        }
        sb.AppendLine();
        sb.AppendLine("### 未确认实体");
        if (fact.UncertainEntities.Count == 0) sb.AppendLine("- 无");
        foreach (var entity in fact.UncertainEntities) sb.AppendLine($"- {FormatEntity(entity)}");
        sb.AppendLine();
        sb.AppendLine("### 已验证事实");
        if (fact.VerifiedFacts.Count == 0) sb.AppendLine("- 无");
        foreach (var item in fact.VerifiedFacts) sb.AppendLine($"- {item}");
        sb.AppendLine();
        sb.AppendLine("### 未确认 / 不得猜测");
        if (fact.Unresolved.Count == 0) sb.AppendLine("- 无");
        foreach (var item in fact.Unresolved) sb.AppendLine($"- {item}");
        sb.AppendLine();
        sb.AppendLine("### 来源");
        if (fact.Sources.Count == 0) sb.AppendLine("- 无");
        foreach (var source in fact.Sources) sb.AppendLine($"- {source}");
        if (!string.IsNullOrWhiteSpace(fact.ToolEvidenceSummary))
        {
            sb.AppendLine();
            sb.AppendLine("### web_extractor 摘要");
            CodeBlock(sb, fact.ToolEvidenceSummary, "text");
        }
    }

    private static string FormatEntity(EntityAlias entity)
    {
        var aliases = entity.Aliases.Count == 0 ? string.Empty : $"（aliases: {string.Join("/", entity.Aliases)}）";
        return $"{entity.Canonical}{aliases}｜{entity.Note}｜status={entity.Status}, confidence={entity.Confidence}";
    }

    private static string ValueOrEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? "（空/未确认）" : value.Trim();

    private static void QuoteBlock(StringBuilder sb, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine("> （空）");
            return;
        }
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            sb.AppendLine("> " + line);
    }

    private static void CodeBlock(StringBuilder sb, string? text, string language)
    {
        sb.AppendLine($"```{language}");
        sb.AppendLine(text ?? string.Empty);
        sb.AppendLine("```");
    }
}
