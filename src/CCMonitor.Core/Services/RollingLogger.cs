namespace CCMonitor.Core.Services;

public sealed class RollingLogger
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly object _gate = new();

    public RollingLogger(string path, long maxBytes = 5 * 1024 * 1024, int maxFiles = 3)
    {
        _path = path;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(Exception exception, string message) => Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            RotateIfNeeded();
            File.AppendAllText(_path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < _maxBytes) return;

        for (var i = _maxFiles - 1; i >= 1; i--)
        {
            var source = $"{_path}.{i}";
            var dest = $"{_path}.{i + 1}";
            if (File.Exists(dest)) File.Delete(dest);
            if (File.Exists(source)) File.Move(source, dest);
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
    }
}
