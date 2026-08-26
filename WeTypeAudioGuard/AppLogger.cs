using System.Text;

namespace WeTypeAudioGuard;

internal sealed class AppLogger
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<bool> _enabled;

    public string LogPath => _path;

    public AppLogger(string baseDir, Func<bool> enabled)
    {
        _path = Path.Combine(baseDir, "WeTypeAudioGuard.log");
        _enabled = enabled;
    }

    public void Write(string message)
    {
        if (!_enabled()) return;
        try
        {
            lock (_gate)
            {
                RotateIfNeeded();
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch { }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return;
            var old = Path.Combine(fi.DirectoryName!, "WeTypeAudioGuard.old.log");
            if (File.Exists(old)) File.Delete(old);
            File.Move(_path, old);
        }
        catch { }
    }
}
