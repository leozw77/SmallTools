using System.Text.Json.Serialization;

namespace AiMovieReviewLab.Core;

public enum ProviderKind
{
    Qwen,
    DeepSeek,
    Glm,
    Custom
}

public sealed class ProviderProfile
{
    public string Name { get; set; } = string.Empty;
    public ProviderKind Kind { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool SupportsWebSearch { get; set; }
    public bool SupportsWebExtractor { get; set; }
    public bool SupportsThinking { get; set; } = true;
    public decimal InputPricePerMillion { get; set; }
    public decimal OutputPricePerMillion { get; set; }
    public decimal CachedInputPricePerMillion { get; set; }

    public override string ToString() => Name;
}

public sealed class LabSettings
{
    public string ProviderName { get; set; } = "Qwen / 百炼";
    public string BaseUrl { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    public string Model { get; set; } = "qwen3.7-flash";
    public bool WebSearch { get; set; } = true;
    public bool ForceSearchWhenNoSubtitle { get; set; } = true;
    public bool Thinking { get; set; }
    public decimal InputPricePerMillion { get; set; } = 0.2m;
    public decimal OutputPricePerMillion { get; set; } = 0.8m;
    public decimal CachedInputPricePerMillion { get; set; } = 0.04m;
}

public sealed class SubtitleCleanResult
{
    public string FilePath { get; set; } = string.Empty;
    public string EncodingName { get; set; } = string.Empty;
    public int RawCharacters { get; set; }
    public int CleanCharacters { get; set; }
    public int ParsedBlocks { get; set; }
    public int KeptLines { get; set; }
    public long ElapsedMs { get; set; }
    public string CleanText { get; set; } = string.Empty;
    [JsonIgnore]
    public string RawText { get; set; } = string.Empty;
}

public sealed class EntityAlias
{
    public string Canonical { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public string Note { get; set; } = string.Empty;
    public string Status { get; set; } = "uncertain";
    public string Confidence { get; set; } = "low";
    public string Evidence { get; set; } = string.Empty;
}

public sealed class CandidateAngle
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
}

public sealed class FactLocalization
{
    public string DoubanReadStatus { get; set; } = "unknown";
    public string SubjectId { get; set; } = string.Empty;
    public string MovieTitle { get; set; } = string.Empty;
    public string MovieIdentity { get; set; } = string.Empty;
    public bool NeedsSceneLocalization { get; set; }
    public string SceneSummary { get; set; } = string.Empty;
    public string SceneConfidence { get; set; } = "unknown";
    public bool FallbackSearchUsed { get; set; }
    public List<EntityAlias> VerifiedEntities { get; set; } = [];
    public List<EntityAlias> UncertainEntities { get; set; } = [];
    public List<string> VerifiedFacts { get; set; } = [];
    public List<CandidateAngle> CandidateAngles { get; set; } = [];
    public List<string> DiscardedInterpretations { get; set; } = [];
    public List<string> Unresolved { get; set; } = [];
    public List<string> Sources { get; set; } = [];
    public string ToolEvidenceSummary { get; set; } = string.Empty;
}

public sealed class FactSnapshot
{
    public int Round { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public string DoubanReadStatus { get; set; } = "unknown";
    public string SceneSummary { get; set; } = string.Empty;
    public string SceneConfidence { get; set; } = "unknown";
    public List<EntityAlias> VerifiedEntities { get; set; } = [];
    public List<string> VerifiedFacts { get; set; } = [];
    public List<string> Unresolved { get; set; } = [];
    public List<string> Sources { get; set; } = [];
}

public sealed class InterviewQuestion
{
    public string Id { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string QuestionType { get; set; } = "normal";
    public string Question { get; set; } = string.Empty;
    public List<string> Options { get; set; } = [];
}

public sealed class InterviewRound
{
    public int Round { get; set; }
    public string Strategy { get; set; } = string.Empty;
    public FactLocalization? FactLocalization { get; set; }
    public List<EntityAlias> Entities { get; set; } = [];
    public List<string> KnownFacts { get; set; } = [];
    public List<InterviewQuestion> Questions { get; set; } = [];
}

public sealed class QuestionAnswer
{
    public int Round { get; set; }
    public string QuestionId { get; set; } = string.Empty;
    public string QuestionType { get; set; } = "normal";
    public string Question { get; set; } = string.Empty;
    public List<string> SelectedOptions { get; set; } = [];
    public string FreeText { get; set; } = string.Empty;
}

public sealed class InterviewSession
{
    public string DoubanUrl { get; set; } = string.Empty;
    public string DoubanSubjectId { get; set; } = string.Empty;
    public string MovieTitle { get; set; } = string.Empty;
    public int Rating { get; set; } = 5;
    public string InitialComment { get; set; } = string.Empty;
    public string SubtitleText { get; set; } = string.Empty;
    public List<EntityAlias> Entities { get; set; } = [];
    public List<string> KnownFacts { get; set; } = [];
    public List<CandidateAngle> CandidateAngles { get; set; } = [];
    public List<InterviewRound> Rounds { get; set; } = [];
    public List<FactSnapshot> FactSnapshots { get; set; } = [];
    public List<QuestionAnswer> Answers { get; set; } = [];
    public string FinalFreeText { get; set; } = string.Empty;
}

public sealed class ToolCallRecord
{
    public string Type { get; set; } = string.Empty;
    public string QueryOrGoal { get; set; } = string.Empty;
    public List<string> Urls { get; set; } = [];
    public string Output { get; set; } = string.Empty;
}

public sealed class AiUsageMetrics
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int CachedPromptTokens { get; set; }
    public int ReasoningTokens { get; set; }
    public int TotalTokens { get; set; }
    public long FirstTokenMs { get; set; }
    public long TotalElapsedMs { get; set; }
    public string FinishReason { get; set; } = string.Empty;
    public int ReasoningCharacters { get; set; }
    public bool WebSearchRequested { get; set; }
    public bool ThinkingEnabled { get; set; }
    public int WebSearchCount { get; set; }
    public int WebExtractorCount { get; set; }
    public string ApiMode { get; set; } = "Chat Completions";
    public string Model { get; set; } = string.Empty;
    public decimal EstimatedCostCny { get; set; }
}

public sealed class AiCallResult
{
    public string Content { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public string ReasoningSummary { get; set; } = string.Empty;
    public List<ToolCallRecord> ToolCalls { get; set; } = [];
    public AiUsageMetrics Metrics { get; set; } = new();
}

public sealed class AiCallRecord
{
    public string Label { get; set; } = string.Empty;
    public DateTime Time { get; set; } = DateTime.Now;
    public string RequestJson { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ReasoningSummary { get; set; } = string.Empty;
    public List<ToolCallRecord> ToolCalls { get; set; } = [];
    public AiUsageMetrics Metrics { get; set; } = new();
}

public sealed class ReviewOutput
{
    public string Review { get; set; } = string.Empty;
    public string MainOpinion { get; set; } = string.Empty;
    public List<string> SupportingOpinions { get; set; } = [];
}

public sealed class SavedTestCase
{
    public string DoubanUrl { get; set; } = string.Empty;
    public string MovieTitle { get; set; } = string.Empty;
    public int Rating { get; set; } = 5;
    public string InitialComment { get; set; } = string.Empty;
    public string SubtitlePath { get; set; } = string.Empty;
    public string WritingStyle { get; set; } = "自然随手";
}
