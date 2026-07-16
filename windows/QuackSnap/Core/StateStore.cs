using System.Text.Json;
using QuackSnap.Protocol;

namespace QuackSnap.Core;

public sealed class Settings
{
    public bool AutoSendScreenshots { get; set; } = true;
    public bool SendAllCopiedImages { get; set; }
    /// <summary>Auto-send copied/highlighted text to the phone's clipboard.</summary>
    public bool SendCopiedText { get; set; }
    /// <summary>Re-encode screenshots as JPEG (smaller, faster) instead of PNG.</summary>
    public bool CompressScreenshots { get; set; }
    /// <summary>Base URL of the E2EE relay used when the phone is unreachable on the LAN.</summary>
    public string? RelayUrl { get; set; }
    public bool FirstRunTipShown { get; set; }
}

public sealed class Device
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    /// <summary>"application" today; "web" once the website receiver exists.</summary>
    public string Kind { get; set; } = DeviceKind.Application;
    public required string Host { get; set; }
    public required int Port { get; set; }
    public required string CertFingerprint { get; set; }
    /// <summary>Base64 DER of the peer's certificate — needed to seal relay envelopes.</summary>
    public string? CertDer { get; set; }
    public DateTime PairedAt { get; set; } = DateTime.UtcNow;
}

public static class DeviceKind
{
    public const string Application = "application";
    public const string Web = "web";
}

public sealed class RecentEntry
{
    public required string Name { get; set; }
    public required string DeviceName { get; set; }
    public required string Status { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public sealed class AppState
{
    public Settings Settings { get; set; } = new();
    public List<Device> Devices { get; set; } = new();
    public List<RecentEntry> Recents { get; set; } = new();
}

/// <summary>JSON-file persistence for settings, paired devices, and recent transfers.</summary>
public sealed class StateStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public AppState State { get; }

    public event Action? Changed;

    public StateStore(string appDataDir)
    {
        _path = Path.Combine(appDataDir, "state.json");
        State = Load();
    }

    private AppState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path), Json.Options) ?? new AppState();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load state, starting fresh", ex);
        }
        return new AppState();
    }

    public void Mutate(Action<AppState> change)
    {
        lock (_gate)
        {
            change(State);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(State, Json.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        Changed?.Invoke();
    }

    public void AddRecent(string name, string deviceName, string status) =>
        Mutate(s =>
        {
            s.Recents.Insert(0, new RecentEntry { Name = name, DeviceName = deviceName, Status = status });
            if (s.Recents.Count > 10) s.Recents.RemoveRange(10, s.Recents.Count - 10);
        });
}
