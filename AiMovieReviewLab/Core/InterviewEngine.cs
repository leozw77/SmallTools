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

        var round = ParseRound(call.Content, roundNumber, session);
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
            1 => "第一轮=锚点定位。三个问题只围绕用户主动说出的锚点，但必须分别取得：触动/看重点、具体感受或焦点、这个锚点对整部电影评价的权重。第三题要能判断它是主要评分依据还是只是一个记忆点。第一轮不要主动发散到其他评价维度。",
            2 => "第二轮=补材料 + 受控发散。仍然恰好3题。Q1/Q2补前一轮真正缺失的信息，不得换措辞重复；Q3必须是 discovery 发散题，询问‘除了刚才说的这些，这部电影还有哪些是你很看重的’，给6-8个彼此不同的中性候选方向供多选。",
            3 => "第三轮=根据用户已经确认的新方向收束。优先追第二轮 discovery 真正勾选或自由补充的内容，再补整体评价/评分理由/余味；不要回头反复追第一轮锚点。第三轮仍然只生成3个AI问题，固定自由题由程序在同一页面作为第4张卡片显示。",
            _ => string.Empty
        };

        var rendered = template
            .Replace("{{ROUND}}", round.ToString(), StringComparison.Ordinal)
            .Replace("{{ROUND_GUIDANCE}}", guidance, StringComparison.Ordinal)
            .Replace("{{OUTPUT_SCHEMA}}", OutputSchema, StringComparison.Ordinal)
            .Trim();

        return rendered + Environment.NewLine + Environment.NewLine + RuntimeRoutingGuard;
    }

    private static string BuildFirstRoundUserPrompt(InterviewSession session, bool allowFallbackSearch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<MANDATORY_FACT_LOCALIZATION>");
        sb.AppendLine("这是第一轮同一次API调用内部的隐藏前置步骤，不要把它变成额外采访轮次。");
        sb.AppendLine($"必须使用 web_extractor 访问一次且只访问一次这个指定豆瓣主体页：{session.DoubanUrl}");
        sb.AppendLine($"豆瓣 Subject ID：{session.DoubanSubjectId}");
        sb.AppendLine("如果已经成功读取该 subject URL，不要再次抓取同一URL。豆瓣页只负责锚定影片身份、人物/角色和与用户初始评论直接相关的基础事实，不要为了完整百科而扩大抓取任务。");
        if (allowFallbackSearch)
        {
            sb.AppendLine("如果豆瓣页不足以定位用户初始评论中的人物/台词/场景，或需要为第二轮发散题采样外部讨论角度，可以调用 web_search，但整个响应最多调用一次 web_search；一次搜索调用中可生成多个精确 query。不要为了重复确认同一事实再次搜索。");
            sb.AppendLine("外部评论只用于提取‘大家常从哪些不同维度谈这部电影’的中性 candidateAngles，例如人物、关系、表演、某类情节、整体情绪、表达等。它们不是事实，更不是用户观点。不要把影评结论塞进 verifiedFacts。 ");
        }
        else
        {
            sb.AppendLine("不要额外进行泛搜索。candidateAngles 可根据已读取的页面信息给出少量中性候选，不确定就少给，不要编造。");
        }
        sb.AppendLine("verifiedFacts 只能写可外部核验的客观陈述，例如谁饰演谁、发生了什么、人物关系。‘体现人性光辉’‘代表悲观主义’‘反战主题’‘完成救赎’之类解释绝对不是 verifiedFacts。");
        sb.AppendLine("如果网页抽取失败，doubanReadStatus 必须写 failed；只读到部分信息写 partial，不得假装成功。");
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

        sb.AppendLine("现在在同一次响应里完成：");
        sb.AppendLine("1) factLocalization：只记录必要事实、已验证实体、未确认点；另外给出6-8个彼此不同的 candidateAngles 作为第二轮发散候选。candidateAngles 必须中性，不能写成用户已经认同的评价。 ");
        sb.AppendLine("2) 第一轮3个锚点问题：问清楚为什么记住/在乎什么、具体感受或焦点、以及该锚点在整部电影评价中的权重。不要向用户索取剧情事实，也不要在第一轮主动展开 candidateAngles。只输出 JSON。");
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
        sb.AppendLine("这些只能作为事实边界。后续轮次不得自行扩写新的电影客观事实；事实不足时，不要把不确定内容写进问题或选项。 ");
        sb.AppendLine("</VERIFIED_FACTS>");
        sb.AppendLine();

        if (round == 2)
        {
            sb.AppendLine("<CANDIDATE_ANGLES_DISCOVERY_ONLY>");
            sb.AppendLine(JsonSerializer.Serialize(session.CandidateAngles, JsonOptions));
            sb.AppendLine("这些来自影片资料/外部讨论，只是第二轮 discovery 题的候选入口，不是用户观点。请选择彼此差异大的方向，改写成简短中性选项；不得写成成熟影评结论。 ");
            sb.AppendLine("</CANDIDATE_ANGLES_DISCOVERY_ONLY>");
            sb.AppendLine();
        }

        sb.AppendLine("<INTERVIEW_TRANSCRIPT>");
        foreach (var answer in session.Answers.OrderBy(x => x.Round).ThenBy(x => x.QuestionId))
        {
            sb.AppendLine($"Round {answer.Round} / {answer.QuestionId} / type={answer.QuestionType}");
            sb.AppendLine("Q: " + answer.Question);
            sb.AppendLine("用户勾选: " + (answer.SelectedOptions.Count == 0 ? "(无)" : string.Join("；", answer.SelectedOptions)));
            sb.AppendLine("用户自由补充: " + (string.IsNullOrWhiteSpace(answer.FreeText) ? "(无)" : answer.FreeText));
            sb.AppendLine();
        }
        sb.AppendLine("</INTERVIEW_TRANSCRIPT>");
        sb.AppendLine();

        if (round == 3)
        {
            var discoveryQuestionIds = session.Rounds
                .SelectMany(x => x.Questions)
                .Where(x => x.QuestionType.Equals("discovery", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var discoveryAnswers = session.Answers
                .Where(x => discoveryQuestionIds.Contains(x.QuestionId))
                .Select(x => new { x.QuestionId, x.SelectedOptions, x.FreeText })
                .ToList();
            sb.AppendLine("<USER_CONFIRMED_DISCOVERY_DIRECTIONS>");
            sb.AppendLine(JsonSerializer.Serialize(discoveryAnswers, JsonOptions));
            sb.AppendLine("只有这里实际被用户勾选或自由补充的方向，才算用户确认的新看重点。未勾选的 candidateAngles 不能进入第三轮，也不能进入最终短评。 ");
            sb.AppendLine("</USER_CONFIRMED_DISCOVERY_DIRECTIONS>");
            sb.AppendLine();
        }

        sb.AppendLine($"现在只生成第 {round}/3 轮的三个AI问题。不得向用户确认电影事实，不要生成下一轮或最终短评。第三轮固定自由收尾题由程序同页显示，模型不要生成。只输出 JSON。 ");
        return sb.ToString().TrimEnd();
    }

    private static InterviewRound ParseRound(string raw, int roundNumber, InterviewSession session)
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
            q.QuestionType = string.IsNullOrWhiteSpace(q.QuestionType) ? "normal" : q.QuestionType.Trim().ToLowerInvariant();
            q.Question = q.Question?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(q.Question)) throw new InvalidOperationException($"{q.Id} 问题为空。 ");
            q.Options ??= [];
            q.Options = CleanOptions(q.Options);
        }

        if (roundNumber == 2)
        {
            var discovery = parsed.Questions[2];
            discovery.QuestionType = "discovery";
            discovery.Purpose = string.IsNullOrWhiteSpace(discovery.Purpose) ? "发散/其他看重点" : discovery.Purpose;
            discovery.Topic = "整部电影的其他看重点";
            discovery.Question = "除了刚才说的这些，这部电影还有哪些是你很看重的？可以多选。";

            var merged = discovery.Options
                .Concat(session.CandidateAngles.Select(x => x.Label))
                .Concat(DiscoveryFallbackOptions)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            discovery.Options = merged;
            if (discovery.Options.Count < 5)
                throw new InvalidOperationException($"{discovery.Id} 发散题只有 {discovery.Options.Count} 个有效候选，至少需要5个。请查看原始响应。 ");
        }

        foreach (var q in parsed.Questions)
        {
            if (q.QuestionType.Equals("discovery", StringComparison.OrdinalIgnoreCase))
            {
                q.Options = q.Options.Take(8).ToList();
                if (q.Options.Count < 5)
                    throw new InvalidOperationException($"{q.Id} 发散题至少需要5个普通选项。 ");
            }
            else
            {
                q.QuestionType = "normal";
                q.Options = q.Options.Take(4).ToList();
                if (q.Options.Count < 3)
                    throw new InvalidOperationException($"{q.Id} 只有 {q.Options.Count} 个有效选项，普通题至少需要3个。 ");
            }
        }

        if (roundNumber == 1 && parsed.FactLocalization is null)
            throw new InvalidOperationException("第一轮没有返回 factLocalization，无法确认模型是否真正理解了指定豆瓣影片。 ");

        return parsed;
    }

    private static List<string> CleanOptions(IEnumerable<string> options) => options
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim())
        .Where(x => !Regex.IsMatch(x, @"^(?:[A-Z][\.、:]?\s*)?(都不符合|其他|其它|以上都不是)$", RegexOptions.IgnoreCase))
        .Select(x => Regex.Replace(x, @"^[A-Z][\.、:]\s*", string.Empty, RegexOptions.IgnoreCase))
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

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

        fact.CandidateAngles ??= [];
        fact.DiscardedInterpretations ??= [];
        fact.VerifiedFacts ??= [];
        fact.VerifiedEntities ??= [];
        fact.UncertainEntities ??= [];
        fact.Unresolved ??= [];

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

        var acceptedFacts = new List<string>();
        foreach (var factText in fact.VerifiedFacts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
        {
            if (LooksInterpretive(factText))
            {
                if (!fact.DiscardedInterpretations.Contains(factText, StringComparer.OrdinalIgnoreCase))
                    fact.DiscardedInterpretations.Add(factText);
                continue;
            }
            if (!acceptedFacts.Contains(factText, StringComparer.OrdinalIgnoreCase)) acceptedFacts.Add(factText);
            if (!session.KnownFacts.Contains(factText, StringComparer.OrdinalIgnoreCase)) session.KnownFacts.Add(factText);
        }
        fact.VerifiedFacts = acceptedFacts;

        var cleanAngles = fact.CandidateAngles
            .Where(x => !string.IsNullOrWhiteSpace(x.Label))
            .Select(x => new CandidateAngle
            {
                Key = string.IsNullOrWhiteSpace(x.Key) ? SlugifyAngle(x.Label) : x.Key.Trim(),
                Label = x.Label.Trim(),
                Evidence = x.Evidence?.Trim() ?? string.Empty
            })
            .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Take(8)
            .ToList();
        fact.CandidateAngles = cleanAngles;
        session.CandidateAngles.Clear();
        session.CandidateAngles.AddRange(cleanAngles);
    }

    private static bool LooksInterpretive(string text)
    {
        var markers = new[]
        {
            "体现", "代表", "象征", "隐喻", "升华", "人性光辉", "悲观视角", "现实主义", "反战主题", "救赎", "价值观", "深刻揭示", "表达了", "反映了"
        };
        return markers.Any(text.Contains);
    }

    private static string SlugifyAngle(string label)
    {
        var chars = label.Where(char.IsLetterOrDigit).Take(18).ToArray();
        return chars.Length == 0 ? "angle" : new string(chars);
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
  "strategy": "一句话说明本轮三个问题分别要获得什么新信息",
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
    "verifiedFacts": ["只放可外部核验的客观事实，最多8条；禁止主题/价值判断/人物心理解释"],
    "candidateAngles": [
      {
        "key": "简短稳定标识",
        "label": "一个中性的评价入口，例如人物关系/演员表现/某类情节/整体情绪，不写成用户观点",
        "evidence": "简短说明这个角度来自哪些外部讨论或影片资料，可空"
      }
    ],
    "discardedInterpretations": [],
    "unresolved": ["仍无法确认的事实；不要猜"],
    "sources": ["若API工具返回来源，在此保留；程序还会把实际工具URL补进来"],
    "toolEvidenceSummary": "可空，程序会写入实际web_extractor摘要"
  },
  "entities": [],
  "knownFacts": [],
  "questions": [
    {
      "id": "R1Q1",
      "purpose": "本题要补的用户信息",
      "topic": "本题采访对象",
      "questionType": "normal | discovery",
      "question": "一句简短自然的问题，一次只问一件主观事情",
      "options": ["候选1", "候选2", "候选3"]
    }
  ]
}
硬要求：每轮 questions 恰好3项。普通题返回3-4个普通选项；第2轮第3题必须 questionType=discovery，并返回6-8个彼此不同的中性候选方向。不要输出“都不符合”“其他”“补充你的想法”，这些由程序固定渲染且不占字母编号。
第一轮必须返回 factLocalization；第二、三轮可省略 factLocalization。第一轮若进行了外部讨论采样，candidateAngles 优先给6-8项。
""";

    private const string RuntimeRoutingGuard = """
<!-- PROGRAM_RUNTIME_ROUTING_V2 -->
# 程序当前采访协议（若与前文/旧自定义Prompt冲突，以本节为准）

总结构永远是：第1轮3题 + 第2轮3题 + 第3轮3题；第3轮页面另外由程序固定显示“还有没有什么刚才没问到，但你特别想说的？”自由输入，不属于新的AI轮次。

一、避免反复追问是最高体验要求
- 同一语义问题即使换措辞也算重复。
- 已选A/B/C或写过自由文字 = ANSWERED；后续只能拿新的材料，不得问一个大概率得到同一答案的问题。
- 选择“都不符合” = CLOSED；该问题框架与其候选方向关闭，后续不得换角度继续逼问。
- “都不符合”同时有自由文字 = CORRECTED；自由文字替换原问题前提，原前提不得复活。

二、三轮发散节奏
- 第1轮：只问用户主动锚点。必须拿到触动点/焦点/锚点对整体评分的权重。锚点是入口，不是整篇短评边界。
- 第2轮：Q1/Q2补缺口；Q3固定为 discovery。Q3问题本身由程序会校正为“除了刚才说的这些，这部电影还有哪些是你很看重的？可以多选。”
- discovery 选项可以参考 candidateAngles，但 candidateAngles 只代表“值得问的方向”，不代表用户认同。6-8个选项必须来自不同维度，避免六个选项其实都在讲同一个主题。
- 第3轮：优先追用户在 discovery 中实际勾选/自由补充的新看重点。未勾选的候选角度一律忽略。若用户没有打开新方向，再用整体评价、评分理由、余味等收束，不得硬发散。

三、网络评论的唯一合法用途
网络评论/外部讨论可以帮助发现“别人通常从哪些维度谈这部电影”，并形成中性的 candidateAngles；绝不能把外部评论的赞美、批评、主题解释直接写成用户观点或 verifiedFacts。

四、选项数量
普通题3-4个选项，保持精准；discovery题6-8个选项，用于多维度兴趣扫描。程序会额外提供不带字母的“都不符合”和自由补充。
<!-- PROGRAM_RUNTIME_ROUTING_V2_END -->
""";

    private static readonly string[] DiscoveryFallbackOptions =
    [
        "人物本身",
        "人物之间的关系",
        "故事和情节推进",
        "演员的表现",
        "某些具体场面",
        "整体情绪和氛围",
        "电影想表达的东西",
        "看完整部片的总体感觉"
    ];

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
            a.QuestionType,
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
        sb.AppendLine("只有 discovery 题里用户实际勾选的选项才是用户信号；未勾选的 candidateAngles 从未成为用户观点，禁止写入短评。 ");
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
