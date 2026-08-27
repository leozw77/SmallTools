using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiMovieReviewLab.Core;

public sealed class InterviewEngine(OpenAiCompatibleClient client)
{
    private readonly OpenAiCompatibleClient _client = client;

    public async Task<(InterviewRound Round, AiCallResult Call)> GenerateRoundAsync(
        InterviewSession session,
        int roundNumber,
        string promptTemplate,
        ProviderProfile provider,
        string apiKey,
        bool thinking,
        bool webSearch,
        bool forceSearch,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(promptTemplate, roundNumber);
        var userPrompt = BuildRoundUserPrompt(session, roundNumber, webSearch);
        var call = await _client.CompleteStreamingAsync(
            provider, apiKey, systemPrompt, userPrompt, thinking,
            webSearch, forceSearch && string.IsNullOrWhiteSpace(session.SubtitleText),
            maxTokens: thinking ? 2500 : 1400, cancellationToken).ConfigureAwait(false);

        var round = ParseRound(call.Content, roundNumber);
        MergeEntities(session, round.Entities);
        MergeFacts(session, round.KnownFacts);
        NormalizeRoundEntities(round, session.Entities);
        return (round, call);
    }

    private static string BuildSystemPrompt(string template, int round)
    {
        var guidance = round switch
        {
            1 => "第一轮=发现观点：三个问题分别优先解决感受来源、真正焦点、与整体评分/总体体验的权重关系。不要急着深挖，不要主动开演员演技/摄影/配乐等新维度。",
            2 => "第二轮=取得材料：只追上一轮真正出现的新线索；三个问题分别优先拿例子、原因、变化/影响/比较。已经回答过的素材禁止重复问。",
            3 => "第三轮=收束观点：原则上不再开启新主题；三个问题优先做权衡、评分解释、最终判断/余味。固定的‘还有什么想说的’由程序单独显示，模型不要生成。",
            _ => string.Empty
        };

        return template
            .Replace("{{ROUND}}", round.ToString(), StringComparison.Ordinal)
            .Replace("{{ROUND_GUIDANCE}}", guidance, StringComparison.Ordinal)
            .Replace("{{OUTPUT_SCHEMA}}", OutputSchema, StringComparison.Ordinal)
            .Trim();
    }

    private static string BuildRoundUserPrompt(InterviewSession session, int round, bool webSearch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<MOVIE_CONTEXT>");
        sb.AppendLine($"电影：{session.MovieTitle}");
        sb.AppendLine($"评分：{session.Rating}/5");
        sb.AppendLine("用户最初主动表达：");
        sb.AppendLine(session.InitialComment);
        sb.AppendLine("</MOVIE_CONTEXT>");
        sb.AppendLine();

        sb.AppendLine("<FACT_SOURCE_PRIORITY>");
        sb.AppendLine(string.IsNullOrWhiteSpace(session.SubtitleText)
            ? "当前没有字幕。若联网已开启，请优先联网核实人物与剧情事实；网络上的影评观点只能当别人的观点，不能作为用户答案。"
            : "已提供完整/清洗字幕。字幕是剧情、对白、人物关系和事件顺序的高权重事实来源；联网只能补基础事实。字幕看不到的镜头、表演动作、音乐等不能自行补写。 ");
        sb.AppendLine($"联网搜索开关：{(webSearch ? "开启" : "关闭")}");
        sb.AppendLine("</FACT_SOURCE_PRIORITY>");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(session.SubtitleText))
        {
            sb.AppendLine("<SUBTITLE_EVIDENCE>");
            sb.AppendLine(session.SubtitleText);
            sb.AppendLine("</SUBTITLE_EVIDENCE>");
            sb.AppendLine();
        }

        sb.AppendLine("<LOCKED_ENTITIES>");
        sb.AppendLine(JsonSerializer.Serialize(session.Entities, JsonOptions));
        sb.AppendLine("规则：已锁定实体在后续轮次不得漂移。用户本来就使用正确演员名时优先保留用户叫法。只有高置信度的语音近音错字才可静默归一。 ");
        sb.AppendLine("</LOCKED_ENTITIES>");
        sb.AppendLine();

        sb.AppendLine("<KNOWN_FACTS>");
        sb.AppendLine(JsonSerializer.Serialize(session.KnownFacts, JsonOptions));
        sb.AppendLine("</KNOWN_FACTS>");
        sb.AppendLine();

        sb.AppendLine("<INTERVIEW_TRANSCRIPT>");
        if (session.Answers.Count == 0)
        {
            sb.AppendLine("尚无上一轮回答。 ");
        }
        else
        {
            foreach (var answer in session.Answers)
            {
                sb.AppendLine($"Round {answer.Round} / {answer.QuestionId}");
                sb.AppendLine("Q: " + answer.Question);
                sb.AppendLine("用户勾选: " + (answer.SelectedOptions.Count == 0 ? "(无)" : string.Join("；", answer.SelectedOptions)));
                sb.AppendLine("用户自由补充: " + (string.IsNullOrWhiteSpace(answer.FreeText) ? "(无)" : answer.FreeText));
                sb.AppendLine();
            }
        }
        sb.AppendLine("</INTERVIEW_TRANSCRIPT>");
        sb.AppendLine();
        sb.AppendLine($"现在只生成第 {round}/3 轮的三个问题。不要生成下一轮，不要生成最终短评，不要生成固定自由收尾题。只输出 JSON。 ");
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

        return parsed;
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

    private static void MergeEntities(InterviewSession session, IEnumerable<EntityAlias> incoming)
    {
        foreach (var entity in incoming)
        {
            entity.Canonical = entity.Canonical?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(entity.Canonical)) continue;
            entity.Aliases ??= [];
            var existing = session.Entities.FirstOrDefault(x => x.Canonical.Equals(entity.Canonical, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                session.Entities.Add(entity);
                continue;
            }
            foreach (var alias in entity.Aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
                if (!existing.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) existing.Aliases.Add(alias);
            if (string.IsNullOrWhiteSpace(existing.Note)) existing.Note = entity.Note;
        }
    }

    private static void MergeFacts(InterviewSession session, IEnumerable<string> facts)
    {
        foreach (var fact in facts.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!session.KnownFacts.Contains(fact.Trim(), StringComparer.OrdinalIgnoreCase)) session.KnownFacts.Add(fact.Trim());
    }

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
        foreach (var entity in entities)
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

    private const string OutputSchema = """
输出必须是单个 JSON object，不要 Markdown，不要前言：
{
  "round": 1,
  "strategy": "一句话说明本轮三个问题分别准备获得什么信息；不能把AI推断写成用户既定观点",
  "entities": [
    {
      "canonical": "已高置信度确认的人物/演员/角色称呼",
      "aliases": ["仅放高置信度语音近音错字或同一实体别称"],
      "note": "简短事实说明"
    }
  ],
  "knownFacts": ["最多5条本轮真正用于理解问题的事实；只写事实，不写影评观点"],
  "questions": [
    {
      "id": "R1Q1",
      "purpose": "感受来源/焦点/权重/例子/原因/变化/影响/权衡/评分/余味之一",
      "topic": "本题正在采访的对象",
      "question": "一句简短自然的问题，一次只问一件核心事情",
      "options": ["朴素候选A", "朴素候选B", "朴素候选C"]
    }
  ]
}
硬要求：questions恰好3项；每题options恰好3项。不要输出“都不符合”“其他”“补充你的想法”，这些由程序固定添加。
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
        sb.AppendLine("<FACTS_ONLY>");
        sb.AppendLine(JsonSerializer.Serialize(session.KnownFacts, JsonOptions));
        sb.AppendLine("</FACTS_ONLY>");
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
