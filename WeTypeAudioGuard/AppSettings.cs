using System.Text.Json;

namespace WeTypeAudioGuard;

internal sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public int VoicePercent { get; set; } = 100;
    public bool StartWithWindows { get; set; } = false;
    public bool LoggingEnabled { get; set; } = true;

    public AppSettings Clone() => new()
    {
        Enabled = Enabled,
        VoicePercent = Math.Clamp(VoicePercent, 1, 100),
        StartWithWindows = StartWithWindows,
        LoggingEnabled = LoggingEnabled
    };
}

internal sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private AppSettings _settings;

    public SettingsStore(string baseDir)
    {
        _path = Path.Combine(baseDir, "settings.json");
        _settings = Load(_path);
    }

    public AppSettings Snapshot()
    {
        lock (_gate) return _settings.Clone();
    }

    public void Update(AppSettings settings)
    {
        settings.VoicePercent = Math.Clamp(settings.VoicePercent, 1, 100);
        lock (_gate)
        {
            _settings = settings.Clone();
            SaveUnsafe();
        }
    }

    private void SaveUnsafe()
    {
        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            if (loaded is null) return new AppSettings();
            loaded.VoicePercent = Math.Clamp(loaded.VoicePercent, 1, 100);
            return loaded;
        }
        catch
        {
            return new AppSettings();
        }
    }
}
