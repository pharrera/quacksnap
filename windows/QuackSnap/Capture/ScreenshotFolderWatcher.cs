using QuackSnap.Core;

namespace QuackSnap.Capture;

/// <summary>
/// Catches Win+PrintScreen, which writes a file to Pictures\Screenshots without
/// always raising a useful clipboard event. Fires on a threadpool thread.
/// </summary>
public sealed class ScreenshotFolderWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;

    public event Action<string>? ScreenshotSaved;

    public ScreenshotFolderWatcher()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        if (!Directory.Exists(folder))
        {
            Logger.Info($"No Screenshots folder at {folder}; folder watcher disabled");
            return;
        }

        _watcher = new FileSystemWatcher(folder, "*.png")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, e) => Task.Run(() => HandleNewFile(e.FullPath));
        Logger.Info($"Watching {folder}");
    }

    private void HandleNewFile(string path)
    {
        // The capture tool may still be writing; wait until we can open it exclusively.
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                ScreenshotSaved?.Invoke(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                Logger.Error($"Watcher could not read {path}", ex);
                return;
            }
        }
        Logger.Error($"Gave up waiting for {path} to become readable");
    }

    public void Dispose() => _watcher?.Dispose();
}
