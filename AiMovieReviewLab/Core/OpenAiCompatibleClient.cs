using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AiMovieReviewLab.Core;

public sealed class OpenAiCompatibleClient(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<AiCallResult> CompleteStreamingAsync(
        ProviderProfile provider,
        string apiKey,
        string systemPrompt,
        string userPrompt,
        bool thinking,
        bool webSearch,
        bool forceSearch,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("API Key 不能为空。 ");
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new InvalidOperationException("Base URL 不能为空。 ");
        if (string.IsNullOrWhiteSpace(provider.Model)) throw new InvalidOperationException("Model 不能为空。 ");

        var requestBody = BuildRequestBody(provider, systemPrompt, userPrompt, thinking, webSearch, forceSearch, maxTokens, includeUsage: true);
        var first = await SendAsync(provider, apiKey.Trim(), requestBody, thinking, webSearch, cancellationToken).ConfigureAwait(false);
        if (first.Success) return first.Result!;

        if (first.StatusCode is 400 or 422)
        {
            requestBody = BuildRequestBody(provider, systemPrompt, userPrompt, thinking, webSearch, forceSearch, maxTokens, includeUsage: false);
            var second = await SendAsync(provider, apiKey.Trim(), requestBody, thinking, webSearch, cancellationToken).ConfigureAwait(false);
            if (second.Success) return second.Result!;
            throw new InvalidOperationException(second.ErrorMessage);
        }

        throw new InvalidOperationException(first.ErrorMessage);
    }

    private async Task<AttemptResult> SendAsync(
        ProviderProfile provider,
        string apiKey,
        Dictionary<string, object?> requestBody,
        bool thinking,
        bool webSearch,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(provider.BaseUrl);
        var requestJson = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return new AttemptResult(false, (int)response.StatusCode, null,
                $"API 请求失败 HTTP {(int)response.StatusCode} {response.ReasonPhrase}\r\n{error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        var raw = new StringBuilder();
        var metrics = new AiUsageMetrics
        {
            Model = provider.Model,
            ThinkingEnabled = thinking,
            WebSearchRequested = webSearch && provider.SupportsWebSearch
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            raw.AppendLine(line);
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                ReadUsage(root, metrics);
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    continue;

                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    metrics.FinishReason = finish.GetString() ?? string.Empty;

                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
                if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                    metrics.ReasoningCharacters += reasoning.GetString()?.Length ?? 0;

                if (delta.TryGetProperty("content", out var piece) && piece.ValueKind == JsonValueKind.String)
                {
                    var value = piece.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        if (metrics.FirstTokenMs == 0) metrics.FirstTokenMs = sw.ElapsedMilliseconds;
                        content.Append(value);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        sw.Stop();
        metrics.TotalElapsedMs = sw.ElapsedMilliseconds;
        if (metrics.TotalTokens == 0) metrics.TotalTokens = metrics.PromptTokens + metrics.CompletionTokens;
        metrics.EstimatedCostCny = EstimateCost(metrics, provider);

        return new AttemptResult(true, (int)response.StatusCode, new AiCallResult
        {
            Content = content.ToString(),
            RequestJson = requestJson,
            RawResponse = raw.ToString(),
            Metrics = metrics
        }, string.Empty);
    }

    private static Dictionary<string, object?> BuildRequestBody(
        ProviderProfile provider,
        string systemPrompt,
        string userPrompt,
        bool thinking,
        bool webSearch,
        bool forceSearch,
        int maxTokens,
        bool includeUsage)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = provider.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            ["stream"] = true,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.35
        };

        if (includeUsage) body["stream_options"] = new { include_usage = true };

        switch (provider.Kind)
        {
            case ProviderKind.Qwen:
                body["enable_thinking"] = thinking;
                if (webSearch && provider.SupportsWebSearch)
                {
                    body["enable_search"] = true;
                    body["search_options"] = new { forced_search = forceSearch };
                }
                break;

            case ProviderKind.DeepSeek:
                body["thinking"] = new { type = thinking ? "enabled" : "disabled" };
                if (thinking) body["reasoning_effort"] = "low";
                break;

            case ProviderKind.Glm:
                body["thinking"] = new { type = thinking ? "enabled" : "disabled" };
                if (webSearch && provider.SupportsWebSearch)
                {
                    body["tools"] = new object[]
                    {
                        new
                        {
                            type = "web_search",
                            web_search = new
                            {
                                enable = true,
                                search_engine = "search_std",
                                search_result = true,
                                count = 5
                            }
                        }
                    };
                    body["tool_choice"] = "auto";
                }
                break;
        }

        return body;
    }

    private static void ReadUsage(JsonElement root, AiUsageMetrics metrics)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
        metrics.PromptTokens = ReadInt(usage, "prompt_tokens", metrics.PromptTokens);
        metrics.CompletionTokens = ReadInt(usage, "completion_tokens", metrics.CompletionTokens);
        metrics.TotalTokens = ReadInt(usage, "total_tokens", metrics.TotalTokens);
        metrics.CachedPromptTokens = Math.Max(metrics.CachedPromptTokens, ReadInt(usage, "prompt_cache_hit_tokens", 0));

        if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails) && promptDetails.ValueKind == JsonValueKind.Object)
            metrics.CachedPromptTokens = Math.Max(metrics.CachedPromptTokens, ReadInt(promptDetails, "cached_tokens", 0));

        if (usage.TryGetProperty("completion_tokens_details", out var completionDetails) && completionDetails.ValueKind == JsonValueKind.Object)
            metrics.ReasoningTokens = Math.Max(metrics.ReasoningTokens, ReadInt(completionDetails, "reasoning_tokens", 0));

        metrics.ReasoningTokens = Math.Max(metrics.ReasoningTokens, ReadInt(usage, "reasoning_tokens", 0));
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return fallback;
    }

    private static decimal EstimateCost(AiUsageMetrics metrics, ProviderProfile provider)
    {
        var cached = Math.Clamp(metrics.CachedPromptTokens, 0, metrics.PromptTokens);
        var normal = Math.Max(0, metrics.PromptTokens - cached);
        return (normal * provider.InputPricePerMillion
                + cached * provider.CachedInputPricePerMillion
                + metrics.CompletionTokens * provider.OutputPricePerMillion) / 1_000_000m;
    }

    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return trimmed;
        return trimmed + "/chat/completions";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record AttemptResult(bool Success, int StatusCode, AiCallResult? Result, string ErrorMessage);
}
