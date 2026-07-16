namespace QuackSnap.Core;

public static class Logger
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string appDataDir)
    {
        var dir = Path.Combine(appDataDir, "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "quacksnap.log");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 2 * 1024 * 1024)
            {
                File.Copy(_path, Path.Combine(dir, "quacksnap.prev.log"), overwrite: true);
                File.Delete(_path);
            }
        }
        catch { /* rotation is best-effort */ }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            try { if (_path != null) File.AppendAllText(_path, line + Environment.NewLine); }
            catch { /* never crash on logging */ }
        }
    }
}
