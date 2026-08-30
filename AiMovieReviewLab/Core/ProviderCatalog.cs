using System.Text.Json;

namespace AiMovieReviewLab.Core;

public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderProfile> Presets { get; } =
    [
        new ProviderProfile
        {
            Name = "Qwen / 百炼",
            Kind = ProviderKind.Qwen,
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = "qwen3.7-flash",
            SupportsWebSearch = true,
            SupportsWebExtractor = true,
            SupportsThinking = true,
            InputPricePerMillion = 0.2m,
            OutputPricePerMillion = 0.8m,
            CachedInputPricePerMillion = 0.04m
        },
        new ProviderProfile
        {
            Name = "DeepSeek",
            Kind = ProviderKind.DeepSeek,
            BaseUrl = "https://api.deepseek.com",
            Model = "deepseek-v4-flash",
            SupportsWebSearch = false,
            SupportsWebExtractor = false,
            SupportsThinking = true,
            InputPricePerMillion = 1m,
            OutputPricePerMillion = 2m,
            CachedInputPricePerMillion = 0.02m
        },
        new ProviderProfile
        {
            Name = "GLM / 智谱",
            Kind = ProviderKind.Glm,
            BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            Model = "glm-4.7-flash",
            SupportsWebSearch = true,
            SupportsWebExtractor = false,
            SupportsThinking = true,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            CachedInputPricePerMillion = 0m
        },
        new ProviderProfile
        {
            Name = "Custom OpenAI-compatible",
            Kind = ProviderKind.Custom,
            BaseUrl = "",
            Model = "",
            SupportsWebSearch = false,
            SupportsWebExtractor = false,
            SupportsThinking = false,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            CachedInputPricePerMillion = 0m
        }
    ];

    public static ProviderProfile Find(string? name) =>
        Presets.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Presets[0];
}

public sealed class LabSettingsStore
{
    private readonly string _path;

    public LabSettingsStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiMovieReviewLab");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
    }

    public string PathOnDisk => _path;

    public LabSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new LabSettings();
            return JsonSerializer.Deserialize<LabSettings>(File.ReadAllText(_path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new LabSettings();
        }
        catch
        {
            return new LabSettings();
        }
    }

    public void Save(LabSettings settings)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
