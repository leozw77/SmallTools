using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiMovieReviewLab.Core;

public sealed class QwenResponsesClient(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<AiCallResult> CompleteWithWebToolsAsync(
        ProviderProfile provider,
        string apiKey,
        string systemPrompt,
        string userPrompt,
        bool thinking,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("API Key 不能为空。");
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new InvalidOperationException("Base URL 不能为空。");
        if (string.IsNullOrWhiteSpace(provider.Model)) throw new InvalidOperationException("Model 不能为空。");

        var endpoint = BuildResponsesEndpoint(provider.BaseUrl);

        // DashScope Responses API currently requires thinking mode when web_extractor is enabled.
        // This is an internal first-round requirement only: the UI Thinking switch still controls
        // the normal Chat Completions calls used by rounds 2/3 and final review generation.
        var effectiveThinking = true;

        var body = new Dictionary<string, object?>
        {
            ["model"] = provider.Model,
            ["input"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            ["tools"] = new object[]
            {
                new { type = "web_search" },
                new { type = "web_extractor" }
            },
            // Do not use tool_choice="required" here. DashScope rejects required mode when more
            // than one tool is provided. InterviewEngine verifies after the response that
            // web_extractor was actually called for the exact requested Douban subject URL.
            ["enable_thinking"] = effectiveThinking
        };

        var requestJson = JsonSerializer.Serialize(body, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Responses API 请求失败 HTTP {(int)response.StatusCode} {response.ReasonPhrase}\r\n{raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var metrics = new AiUsageMetrics
        {
            Model = provider.Model,
            ThinkingEnabled = effectiveThinking,
            WebSearchRequested = true,
            TotalElapsedMs = sw.ElapsedMilliseconds,
            ApiMode = thinking
                ? "Responses + web_extractor (thinking on)"
                : "Responses + web_extractor (thinking forced for extractor)"
        };

        var tools = new List<ToolCallRecord>();
        var content = new StringBuilder();
        var reasoningSummary = new StringBuilder();

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString() ?? string.Empty
                    : string.Empty;

                if (type == "message")
                {
                    if (!item.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array) continue;
                    foreach (var block in blocks.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var bt) && bt.GetString() == "output_text" &&
                            block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            content.Append(text.GetString());
                    }
                    continue;
                }

                if (type == "web_search_call")
                {
                    var record = new ToolCallRecord { Type = "web_search" };
                    if (item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
                    {
                        if (action.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String)
                            record.QueryOrGoal = query.GetString() ?? string.Empty;
                        if (action.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var source in sources.EnumerateArray())
                                if (source.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString()))
                                    record.Urls.Add(url.GetString()!);
                        }
                    }
                    tools.Add(record);
                    continue;
                }

                if (type == "web_extractor_call")
                {
                    var record = new ToolCallRecord { Type = "web_extractor" };
                    if (item.TryGetProperty("goal", out var goal) && goal.ValueKind == JsonValueKind.String)
                        record.QueryOrGoal = goal.GetString() ?? string.Empty;
                    if (item.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var url in urls.EnumerateArray())
                            if (url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString()))
                                record.Urls.Add(url.GetString()!);
                    }
                    if (item.TryGetProperty("output", out var toolOutput) && toolOutput.ValueKind == JsonValueKind.String)
                        record.Output = toolOutput.GetString() ?? string.Empty;
                    tools.Add(record);
                    continue;
                }

                if (type == "reasoning" && item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in summary.EnumerateArray())
                        if (entry.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            reasoningSummary.AppendLine(text.GetString());
                }
            }
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            metrics.PromptTokens = ReadInt(usage, "input_tokens");
            metrics.CompletionTokens = ReadInt(usage, "output_tokens");
            metrics.TotalTokens = ReadInt(usage, "total_tokens", metrics.PromptTokens + metrics.CompletionTokens);
            if (usage.TryGetProperty("input_tokens_details", out var inputDetails) && inputDetails.ValueKind == JsonValueKind.Object)
                metrics.CachedPromptTokens = ReadInt(inputDetails, "cached_tokens");
            if (usage.TryGetProperty("output_tokens_details", out var outputDetails) && outputDetails.ValueKind == JsonValueKind.Object)
                metrics.ReasoningTokens = ReadInt(outputDetails, "reasoning_tokens");
            if (usage.TryGetProperty("x_tools", out var xTools) && xTools.ValueKind == JsonValueKind.Object)
            {
                metrics.WebSearchCount = ReadToolCount(xTools, "web_search");
                metrics.WebExtractorCount = ReadToolCount(xTools, "web_extractor");
            }
        }

        if (metrics.WebSearchCount == 0) metrics.WebSearchCount = tools.Count(x => x.Type == "web_search");
        if (metrics.WebExtractorCount == 0) metrics.WebExtractorCount = tools.Count(x => x.Type == "web_extractor");
        metrics.EstimatedCostCny = EstimateCost(metrics, provider);

        return new AiCallResult
        {
            Content = content.ToString(),
            RequestJson = requestJson,
            RawResponse = raw,
            Metrics = metrics,
            ToolCalls = tools,
            ReasoningSummary = reasoningSummary.ToString().Trim()
        };
    }

    private static string BuildResponsesEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)) return trimmed;
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/chat/completions".Length];
        return trimmed + "/responses";
    }

    private static int ReadInt(JsonElement element, string name, int fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : fallback;
    }

    private static int ReadToolCount(JsonElement xTools, string name)
    {
        if (!xTools.TryGetProperty(name, out var tool) || tool.ValueKind != JsonValueKind.Object) return 0;
        return ReadInt(tool, "count");
    }

    private static decimal EstimateCost(AiUsageMetrics metrics, ProviderProfile provider)
    {
        var cached = Math.Clamp(metrics.CachedPromptTokens, 0, metrics.PromptTokens);
        var normal = Math.Max(0, metrics.PromptTokens - cached);
        return (normal * provider.InputPricePerMillion
                + cached * provider.CachedInputPricePerMillion
                + metrics.CompletionTokens * provider.OutputPricePerMillion) / 1_000_000m;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
