using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiMovieReviewLab.Core;

public sealed class InterviewEngine(OpenAiCompatibleClient client, QwenResponsesClient responsesClient)
{
    private readonly OpenAiCompatibleClient _client = client;
    private readonly QwenResponsesClient _responsesClient = responsesClient;

    public async Task<(InterviewRound Round, AiCallResult Call)> GenerateRoundAsync(
        InterviewSession session,
        int roundNumber,
        string promptTemplate,
        ProviderProfile provider,
        string apiKey,
        bool thinking,
        bool allowFallbackSearch,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(promptTemplate, roundNumber);
        AiCallResult call;

        if (roundNumber == 1 && provider.SupportsWebExtractor && provider.Kind == ProviderKind.Qwen)
        {
            var userPrompt = BuildFirstRoundUserPrompt(session, allowFallbackSearch);
            call = await _responsesClient.CompleteWithWebToolsAsync(
                provider, apiKey, systemPrompt, userPrompt, thinking, cancellationToken).ConfigureAwait(false);
            EnsureDoubanExtractorWasCalled(session, call);
        }
        else
        {
            var userPrompt = BuildRoundUserPrompt(session, roundNumber);
            var useSearch = roundNumber == 1 && provider.SupportsWebSearch && allowFallbackSearch;
            call = await _client.CompleteStreamingAsync(
                provider, apiKey, systemPrompt, userPrompt, thinking,
                webSearch: useSearch, forceSearch: useSearch,
                maxTokens: thinking ? 2500 : 1500, cancellationToken).ConfigureAwait(false);
            call.Metrics.ApiMode = roundNumber == 1
                ? "Chat Completions / provider search fallback"
                : "Chat Completions / verified facts only";
        }

        var round = ParseRound(call.Content, roundNumber);
        if (roundNumber == 1)
        {
            round.FactLocalization ??= new FactLocalization
            {
                DoubanReadStatus = provider.SupportsWebExtractor ? "unknown" : "not_supported",
                SubjectId = session.DoubanSubjectId,
                MovieTitle = session.MovieTitle
            };
            AttachActualToolTrace(round.FactLocalization, call);
            ApplyFirstRoundFacts(session, round.FactLocalization);
        }

        NormalizeRoundEntities(round, session.Entities);
        session.FactSnapshots.Add(CreateSnapshot(session, round));
        return (round, call);
    }

    private static string BuildSystemPrompt(string template, int round)
    {
        var guidance = round switch
        {
            1 => "第一轮=先完成隐藏事实定位，再发现观点。绝对禁止向用户询问本应由AI自己核实的电影客观事实。三个问题只问用户感受：感受来源、真正焦点、与整体评分/总体体验的权重关系。",
            2 => "第二轮=取得材料：只追上一轮用户真正出现的新线索；三个问题分别优先拿例子、原因、变化/影响/比较。不得重新联网制造新解释，不得重复已经得到的信息。",
            3 => "第三轮=收束观点：原则上不再开启新主题；三个问题优先做权衡、评分解释、最终判断/余味。固定的‘还有什么想说的’由程序单独显示，模型不要生成。",
            _ => string.Empty
        };

        return template
            .Replace("{{ROUND}}", round.ToString(), StringComparison.Ordinal)
            .Replace("{{ROUND_GUIDANCE}}", guidance, StringComparison.Ordinal)
            .Replace("{{OUTPUT_SCHEMA}}", OutputSchema, StringComparison.Ordinal)
            .Trim();
    }

    private static string BuildFirstRoundUserPrompt(InterviewSession session, bool allowFallbackSearch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<MANDATORY_FACT_LOCALIZATION>");
        sb.AppendLine("这是第一轮同一次API调用内部的隐藏前置步骤，不要把它变成额外采访轮次。");
        sb.AppendLine($"你必须先使用 web_extractor 访问这个指定豆瓣主体页：{session.DoubanUrl}");
        sb.AppendLine($"豆瓣 Subject ID：{session.DoubanSubjectId}");
        sb.AppendLine("不要只搜索片名来替代读取这个URL。先用指定豆瓣页面确认电影身份、演员/角色、简介等客观资料。");
        sb.AppendLine(allowFallbackSearch
            ? "如果豆瓣主体页不足以定位用户初始评论提到的具体人物/台词/场景，再使用 web_search 精确补查；补查只能补事实，网络影评观点不可成为用户观点。"
            : "不要额外进行泛搜索；若豆瓣页不足以定位具体场景，将未确认部分写入 unresolved，采访问题仍只能问用户主观感受，不得让用户替AI补剧情事实。");
        sb.AppendLine("如果网页抽取失败，doubanReadStatus 必须明确写 failed；如果只读到部分信息写 partial，不得假装成功。");
        sb.AppendLine("无论事实定位结果如何，都禁止问用户：‘这句话是谁对谁说的/当时发生了什么/某角色当时是什么状态’之类客观剧情问题。");
        sb.AppendLine("</MANDATORY_FACT_LOCALIZATION>");
        sb.AppendLine();

        sb.AppendLine("<USER_INPUT>");
        sb.AppendLine($"评分：{session.Rating}/5");
        sb.AppendLine("用户最初主动表达：");
        sb.AppendLine(session.InitialComment);
        sb.AppendLine("</USER_INPUT>");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(session.SubtitleText))
        {
            sb.AppendLine("<SUBTITLE_EVIDENCE>");
            sb.AppendLine("字幕是对白、人物关系、事件顺序的高权重事实来源；但不能单独证明眼神、动作、镜头、配乐等视觉/声音细节。");
            sb.AppendLine(session.SubtitleText);
            sb.AppendLine("</SUBTITLE_EVIDENCE>");
            sb.AppendLine();
        }

        sb.AppendLine("现在在同一次响应里完成两件事：");
        sb.AppendLine("1) 输出 factLocalization，清楚记录豆瓣读取状态、人物/场景定位、来源、未确认点；只有真正有事实依据且高置信度的实体才能放 verifiedEntities。");
        sb.AppendLine("2) 基于已经确认的事实，直接输出第一轮3个主观采访问题。不要向用户索取剧情事实。只输出 JSON。");
        return sb.ToString().TrimEnd();
    }

    private static string BuildRoundUserPrompt(InterviewSession session, int round)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<MOVIE_CONTEXT>");
        sb.AppendLine($"豆瓣：{session.DoubanUrl}");
        sb.AppendLine($"电影：{session.MovieTitle}");
        sb.AppendLine($"评分：{session.Rating}/5");
        sb.AppendLine("用户最初主动表达：");
        sb.AppendLine(session.InitialComment);
        sb.AppendLine("</MOVIE_CONTEXT>");
        sb.AppendLine();

        sb.AppendLine("<VERIFIED_ENTITIES>");
        sb.AppendLine(JsonSerializer.Serialize(session.Entities, JsonOptions));
        sb.AppendLine("这些是第一轮事实定位后允许锁定的高置信度实体。不得擅自把两个不同人物合并。用户本来使用正确演员名时优先沿用用户叫法。 ");
        sb.AppendLine("</VERIFIED_ENTITIES>");
        sb.AppendLine();

        sb.AppendLine("<VERIFIED_FACTS>");
        sb.AppendLine(JsonSerializer.Serialize(session.KnownFacts, JsonOptions));
        sb.AppendLine("后续轮次不得自行扩写新的电影客观事实；事实不足时，避免把不确定内容写进问题或选项。 ");
        sb.AppendLine("</VERIFIED_FACTS>");
        sb.AppendLine();

        sb.AppendLine("<INTERVIEW_TRANSCRIPT>");
        foreach (var answer in session.Answers.OrderBy(x => x.Round).ThenBy(x => x.QuestionId))
        {
            sb.AppendLine($"Round {answer.Round} / {answer.QuestionId}");
            sb.AppendLine("Q: " + answer.Question);
            sb.AppendLine("用户勾选: " + (answer.SelectedOptions.Count == 0 ? "(无)" : string.Join("；", answer.SelectedOptions)));
            sb.AppendLine("用户自由补充: " + (string.IsNullOrWhiteSpace(answer.FreeText) ? "(无)" : answer.FreeText));
            sb.AppendLine();
        }
        sb.AppendLine("</INTERVIEW_TRANSCRIPT>");
        sb.AppendLine();
        sb.AppendLine($"现在只生成第 {round}/3 轮的三个问题。不得向用户确认电影事实，不要生成下一轮、最终短评或固定自由收尾题。只输出 JSON。 ");
        return sb.ToString().TrimEnd();
    }

    private static InterviewRound ParseRound(string raw, int roundNumber)
    {
        var json = ExtractJson(raw);
        var parsed = JsonSerializer.Deserialize<InterviewRound>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("模型 JSON 无法解析为 InterviewRound。 ");

        parsed.Round = roundNumber;
        parsed.Entities ??= [];
        parsed.KnownFacts ??= [];
        parsed.Questions ??= [];
        if (parsed.Questions.Count < 3)
            throw new InvalidOperationException($"模型只返回 {parsed.Questions.Count} 道问题，本轮必须有3道。请查看原始响应。 ");
        if (parsed.Questions.Count > 3) parsed.Questions = parsed.Questions.Take(3).ToList();

        for (var i = 0; i < parsed.Questions.Count; i++)
        {
            var q = parsed.Questions[i];
            q.Id = $"R{roundNumber}Q{i + 1}";
            q.Question = q.Question?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(q.Question)) throw new InvalidOperationException($"{q.Id} 问题为空。 ");
            q.Options ??= [];
            q.Options = q.Options
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !Regex.IsMatch(x.Trim(), @"^(D[\.、:]?\s*)?(都不符合|其他|其它|以上都不是)"))
                .Select(x => Regex.Replace(x.Trim(), @"^[ABC][\.、:]\s*", string.Empty, RegexOptions.IgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            if (q.Options.Count != 3)
                throw new InvalidOperationException($"{q.Id} 有 {q.Options.Count} 个有效选项，本轮要求模型返回3个普通选项；D与自由补充由程序固定渲染。 ");
        }

        if (roundNumber == 1 && parsed.FactLocalization is null)
            throw new InvalidOperationException("第一轮没有返回 factLocalization，无法确认模型是否真正理解了指定豆瓣影片。 ");

        return parsed;
    }

    private static void EnsureDoubanExtractorWasCalled(InterviewSession session, AiCallResult call)
    {
        var extractorCalls = call.ToolCalls.Where(x => x.Type == "web_extractor").ToList();
        if (extractorCalls.Count == 0)
            throw new InvalidOperationException("第一轮要求强制读取豆瓣链接，但 Responses API 没有调用 web_extractor。采访已停止，避免在错误事实基础上继续。 ");

        var subjectMarker = $"/subject/{session.DoubanSubjectId}";
        var matched = extractorCalls.Any(x => x.Urls.Any(url => url.Contains(subjectMarker, StringComparison.OrdinalIgnoreCase)));
        if (!matched)
            throw new InvalidOperationException($"web_extractor 被调用了，但没有证据表明它读取了指定豆瓣 Subject {session.DoubanSubjectId}。采访已停止。 ");
    }

    private static void AttachActualToolTrace(FactLocalization fact, AiCallResult call)
    {
        var actualSources = call.ToolCalls.SelectMany(x => x.Urls)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var url in actualSources)
            if (!fact.Sources.Contains(url, StringComparer.OrdinalIgnoreCase)) fact.Sources.Add(url);

        var extractorEvidence = call.ToolCalls.Where(x => x.Type == "web_extractor" && !string.IsNullOrWhiteSpace(x.Output))
            .Select(x => x.Output.Trim())
            .ToList();
        if (extractorEvidence.Count > 0)
        {
            var joined = string.Join("\n---\n", extractorEvidence);
            fact.ToolEvidenceSummary = joined.Length > 1800 ? joined[..1800] + "…" : joined;
        }
        fact.FallbackSearchUsed = call.Metrics.WebSearchCount > 0;
    }

    private static void ApplyFirstRoundFacts(InterviewSession session, FactLocalization fact)
    {
        if (!string.IsNullOrWhiteSpace(fact.SubjectId) && !fact.SubjectId.Equals(session.DoubanSubjectId, StringComparison.OrdinalIgnoreCase))
            fact.Unresolved.Add($"模型返回的 Subject ID {fact.SubjectId} 与用户链接 {session.DoubanSubjectId} 不一致。 ");

        if (!string.IsNullOrWhiteSpace(fact.MovieTitle)) session.MovieTitle = fact.MovieTitle.Trim();

        foreach (var entity in fact.VerifiedEntities)
        {
            entity.Canonical = entity.Canonical?.Trim() ?? string.Empty;
            entity.Aliases ??= [];
            var canLock = entity.Status.Equals("verified", StringComparison.OrdinalIgnoreCase)
                          && entity.Confidence.Equals("high", StringComparison.OrdinalIgnoreCase)
                          && !string.IsNullOrWhiteSpace(entity.Canonical)
                          && !string.IsNullOrWhiteSpace(entity.Evidence);
            if (!canLock)
            {
                if (!fact.UncertainEntities.Any(x => x.Canonical.Equals(entity.Canonical, StringComparison.OrdinalIgnoreCase)))
                    fact.UncertainEntities.Add(entity);
                continue;
            }

            var existing = session.Entities.FirstOrDefault(x => x.Canonical.Equals(entity.Canonical, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                session.Entities.Add(entity);
                continue;
            }
            foreach (var alias in entity.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
                if (!existing.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) existing.Aliases.Add(alias);
        }

        foreach (var factText in fact.VerifiedFacts.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!session.KnownFacts.Contains(factText.Trim(), StringComparer.OrdinalIgnoreCase)) session.KnownFacts.Add(factText.Trim());
    }

    private static FactSnapshot CreateSnapshot(InterviewSession session, InterviewRound round)
    {
        var localization = round.FactLocalization;
        return new FactSnapshot
        {
            Round = round.Round,
            DoubanReadStatus = localization?.DoubanReadStatus ?? session.FactSnapshots.LastOrDefault()?.DoubanReadStatus ?? "unknown",
            SceneSummary = localization?.SceneSummary ?? session.FactSnapshots.LastOrDefault()?.SceneSummary ?? string.Empty,
            SceneConfidence = localization?.SceneConfidence ?? session.FactSnapshots.LastOrDefault()?.SceneConfidence ?? "unknown",
            VerifiedEntities = session.Entities.Select(CloneEntity).ToList(),
            VerifiedFacts = session.KnownFacts.ToList(),
            Unresolved = localization?.Unresolved.ToList() ?? session.FactSnapshots.LastOrDefault()?.Unresolved.ToList() ?? [],
            Sources = localization?.Sources.ToList() ?? session.FactSnapshots.LastOrDefault()?.Sources.ToList() ?? []
        };
    }

    private static EntityAlias CloneEntity(EntityAlias x) => new()
    {
        Canonical = x.Canonical,
        Aliases = x.Aliases.ToList(),
        Note = x.Note,
        Status = x.Status,
        Confidence = x.Confidence,
        Evidence = x.Evidence
    };

    private static void NormalizeRoundEntities(InterviewRound round, IReadOnlyList<EntityAlias> entities)
    {
        foreach (var q in round.Questions)
        {
            q.Question = NormalizeText(q.Question, entities);
            for (var i = 0; i < q.Options.Count; i++) q.Options[i] = NormalizeText(q.Options[i], entities);
        }
    }

    private static string NormalizeText(string text, IReadOnlyList<EntityAlias> entities)
    {
        var result = text;
        foreach (var entity in entities.Where(x => x.Status.Equals("verified", StringComparison.OrdinalIgnoreCase)
                                                    && x.Confidence.Equals("high", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(entity.Canonical)) continue;
            foreach (var alias in entity.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (alias.Equals(entity.Canonical, StringComparison.OrdinalIgnoreCase)) continue;
                result = result.Replace(alias, entity.Canonical, StringComparison.OrdinalIgnoreCase);
            }
        }
        return result;
    }

    private static string ExtractJson(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) text = text[..lastFence];
        }
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last <= first) throw new InvalidOperationException("模型没有返回可识别的 JSON object。 ");
        return text[first..(last + 1)];
    }

    private const string OutputSchema = """
输出必须是单个 JSON object，不要 Markdown，不要前言：
{
  "round": 1,
  "strategy": "一句话说明本轮三个问题分别要获得什么用户信息，不得把AI推断写成用户既定观点",
  "factLocalization": {
    "doubanReadStatus": "success | partial | failed | not_supported",
    "subjectId": "必须来自指定豆瓣URL",
    "movieTitle": "读取/确认后的影片名",
    "movieIdentity": "一句客观身份说明",
    "needsSceneLocalization": true,
    "sceneSummary": "仅写已确认的用户所指场景/台词关系；不能确认就留空",
    "sceneConfidence": "high | medium | low | not_applicable",
    "fallbackSearchUsed": false,
    "verifiedEntities": [
      {
        "canonical": "高置信度人物/演员/角色称呼",
        "aliases": ["仅放能确认属于同一实体的语音近音错字/别称"],
        "note": "客观身份",
        "status": "verified",
        "confidence": "high",
        "evidence": "说明依据来自豆瓣页/字幕/具体搜索来源，不写影评解释"
      }
    ],
    "uncertainEntities": [],
    "verifiedFacts": ["最多8条真正有来源支持的事实"],
    "unresolved": ["仍无法确认的事实；不要猜"],
    "sources": ["若API工具返回来源，在此保留；程序还会把实际工具URL补进来"],
    "toolEvidenceSummary": "可空，程序会写入实际web_extractor摘要"
  },
  "entities": [],
  "knownFacts": [],
  "questions": [
    {
      "id": "R1Q1",
      "purpose": "感受来源/焦点/权重/例子/原因/变化/影响/权衡/评分/余味之一",
      "topic": "本题采访对象",
      "question": "一句简短自然的问题，一次只问一件主观事情",
      "options": ["朴素候选A", "朴素候选B", "朴素候选C"]
    }
  ]
}
硬要求：questions恰好3项；每题options恰好3项。不要输出“都不符合”“其他”“补充你的想法”，这些由程序固定添加。
第一轮必须返回 factLocalization；第二、三轮可省略 factLocalization。
""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class ReviewEngine(OpenAiCompatibleClient client)
{
    private readonly OpenAiCompatibleClient _client = client;

    public async Task<(ReviewOutput Output, AiCallResult Call)> GenerateAsync(
        InterviewSession session,
        string promptTemplate,
        string writingStyle,
        ProviderProfile provider,
        string apiKey,
        bool thinking,
        CancellationToken cancellationToken = default)
    {
        var system = promptTemplate
            .Replace("{{WRITING_STYLE}}", writingStyle, StringComparison.Ordinal)
            .Replace("{{OUTPUT_SCHEMA}}", ReviewSchema, StringComparison.Ordinal)
            .Trim();
        var user = BuildUserPrompt(session, writingStyle);
        var call = await _client.CompleteStreamingAsync(
            provider, apiKey, system, user, thinking,
            webSearch: false, forceSearch: false,
            maxTokens: thinking ? 2500 : 1200, cancellationToken).ConfigureAwait(false);
        call.Metrics.ApiMode = "Chat Completions / final review";

        var json = ExtractJson(call.Content);
        var output = JsonSerializer.Deserialize<ReviewOutput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? throw new InvalidOperationException("模型 JSON 无法解析为 ReviewOutput。 ");
        output.Review = output.Review?.Trim() ?? string.Empty;
        if (output.Review.Length > 330) output.Review = output.Review[..330].TrimEnd();
        return (output, call);
    }

    private static string BuildUserPrompt(InterviewSession session, string style)
    {
        var authored = new List<string> { "初始评论：" + session.InitialComment };
        foreach (var a in session.Answers.Where(x => !string.IsNullOrWhiteSpace(x.FreeText)))
            authored.Add($"{a.QuestionId}自由补充：{a.FreeText}");
        if (!string.IsNullOrWhiteSpace(session.FinalFreeText)) authored.Add("最终自由发言：" + session.FinalFreeText);

        var selected = session.Answers.Select(a => new
        {
            a.Round,
            a.QuestionId,
            a.Question,
            selected = a.SelectedOptions,
            freeText = a.FreeText
        }).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"豆瓣：{session.DoubanUrl}");
        sb.AppendLine($"电影：{session.MovieTitle}");
        sb.AppendLine($"评分：{session.Rating}/5");
        sb.AppendLine($"文风：{style}");
        sb.AppendLine();
        sb.AppendLine("<USER_AUTHORED_HIGHEST_PRIORITY>");
        foreach (var x in authored) sb.AppendLine("- " + x);
        sb.AppendLine("</USER_AUTHORED_HIGHEST_PRIORITY>");
        sb.AppendLine();
        sb.AppendLine("<INTERVIEW_ANSWERS>");
        sb.AppendLine(JsonSerializer.Serialize(selected, JsonOptions));
        sb.AppendLine("</INTERVIEW_ANSWERS>");
        sb.AppendLine();
        sb.AppendLine("<VERIFIED_FACTS_ONLY>");
        sb.AppendLine(JsonSerializer.Serialize(new { entities = session.Entities, facts = session.KnownFacts }, JsonOptions));
        sb.AppendLine("只能使用这些已验证事实避免事实错误。不得把第一轮AI的猜测、网络影评或uncertain实体写进短评。 ");
        sb.AppendLine("</VERIFIED_FACTS_ONLY>");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(session.SubtitleText))
        {
            sb.AppendLine("<SUBTITLE_EVIDENCE>");
            sb.AppendLine(session.SubtitleText);
            sb.AppendLine("</SUBTITLE_EVIDENCE>");
        }
        sb.AppendLine("用户自由文字若与勾选项冲突，以自由文字为准。不得把AI/网络解读当成用户观点。只输出 JSON。 ");
        return sb.ToString().TrimEnd();
    }

    private static string ExtractJson(string raw)
    {
        var text = raw.Trim();
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last <= first) throw new InvalidOperationException("最终短评响应中没有 JSON object。 ");
        return text[first..(last + 1)];
    }

    private const string ReviewSchema = """
只输出：
{
  "mainOpinion": "用户最终最重要的判断",
  "supportingOpinions": ["最多4条辅助观点"],
  "review": "最终短评，最多330个中文字符"
}
""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
