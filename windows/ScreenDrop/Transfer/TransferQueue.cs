using System.Security.Cryptography;
using System.Text.Json;
using ScreenDrop.Core;
using ScreenDrop.Protocol;

namespace ScreenDrop.Transfer;

public sealed class TransferItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string DeviceId { get; set; }
    /// <summary>SHA-256 hex of the payload bytes; also the content id on the wire.</summary>
    public required string FileId { get; set; }
    public required string Name { get; set; }
    public string Mime { get; set; } = "image/png";
    public required long Size { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Disk-backed outbound queue. Payload bytes are stored once per unique file;
/// one queue item exists per (file, target device). Survives restarts, so
/// screenshots taken while the phone is away are delivered when it returns.
/// </summary>
public sealed class TransferQueue
{
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300),
    };

    private readonly string _dir;
    private readonly string _payloadDir;
    private readonly string _indexPath;
    private readonly object _gate = new();
    private readonly List<TransferItem> _items;

    public event Action? Changed;

    public TransferQueue(string appDataDir)
    {
        _dir = Path.Combine(appDataDir, "queue");
        _payloadDir = Path.Combine(_dir, "payloads");
        Directory.CreateDirectory(_payloadDir);
        _indexPath = Path.Combine(_dir, "queue.json");
        _items = Load();
    }

    private List<TransferItem> Load()
    {
        try
        {
            if (File.Exists(_indexPath))
                return JsonSerializer.Deserialize<List<TransferItem>>(File.ReadAllText(_indexPath), Json.Options) ?? new();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load queue index", ex);
        }
        return new();
    }

    public string PayloadPath(string fileId) => Path.Combine(_payloadDir, fileId + ".bin");

    public int PendingCount { get { lock (_gate) return _items.Count; } }

    /// <summary>Stores the payload and creates one queue item per target device.</summary>
    public void Enqueue(byte[] payload, string name, string mime, IEnumerable<Device> devices)
    {
        string fileId = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        lock (_gate)
        {
            var path = PayloadPath(fileId);
            if (!File.Exists(path)) File.WriteAllBytes(path, payload);

            foreach (var device in devices)
            {
                if (_items.Any(i => i.FileId == fileId && i.DeviceId == device.Id)) continue;
                _items.Add(new TransferItem
                {
                    DeviceId = device.Id,
                    FileId = fileId,
                    Name = name,
                    Mime = mime,
                    Size = payload.Length,
                });
            }
            Persist();
        }
        Changed?.Invoke();
    }

    public List<TransferItem> Due(string deviceId)
    {
        lock (_gate)
            return _items.Where(i => i.DeviceId == deviceId && i.NextAttemptAt <= DateTime.UtcNow)
                         .OrderBy(i => i.CreatedAt).ToList();
    }

    public DateTime? NextDueAt()
    {
        lock (_gate)
            return _items.Count == 0 ? null : _items.Min(i => i.NextAttemptAt);
    }

    public void MarkSent(TransferItem item)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Id == item.Id);
            if (_items.All(i => i.FileId != item.FileId))
            {
                try { File.Delete(PayloadPath(item.FileId)); }
                catch (Exception ex) { Logger.Error("Payload cleanup failed", ex); }
            }
            Persist();
        }
        Changed?.Invoke();
    }

    public void MarkFailed(TransferItem item)
    {
        lock (_gate)
        {
            item.Attempts++;
            item.NextAttemptAt = DateTime.UtcNow + Backoff[Math.Min(item.Attempts, Backoff.Length - 1)];
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Called when the device comes back online: retry everything now.</summary>
    public void ResetBackoff(string deviceId)
    {
        lock (_gate)
        {
            foreach (var item in _items.Where(i => i.DeviceId == deviceId))
                item.NextAttemptAt = DateTime.UtcNow;
            Persist();
        }
        Changed?.Invoke();
    }

    public void DropForDevice(string deviceId)
    {
        lock (_gate)
        {
            var removed = _items.Where(i => i.DeviceId == deviceId).ToList();
            _items.RemoveAll(i => i.DeviceId == deviceId);
            foreach (var item in removed.Where(r => _items.All(i => i.FileId != r.FileId)))
            {
                try { File.Delete(PayloadPath(item.FileId)); }
                catch { /* best effort */ }
            }
            Persist();
        }
        Changed?.Invoke();
    }

    private void Persist()
    {
        var tmp = _indexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json.Options));
        File.Move(tmp, _indexPath, overwrite: true);
    }
}
