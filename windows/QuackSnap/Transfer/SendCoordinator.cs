using QuackSnap.Core;

namespace QuackSnap.Transfer;

public sealed record TransferProgress(Guid ItemId, string Name, string DeviceName, long Sent, long Total, bool Done, bool Failed);

/// <summary>
/// Owns the send loop: wakes when items are enqueued or retries come due, picks a
/// transport by device kind, and reports progress. Fully event-driven — sleeps
/// forever when the queue is empty.
/// </summary>
public sealed class SendCoordinator : IDisposable
{
    private readonly TransferQueue _queue;
    private readonly StateStore _store;
    private readonly Dictionary<string, ITransport> _transports;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, bool> _reachable = new();

    public event Action<TransferProgress>? Progress;
    public event Action? ReachabilityChanged;

    private readonly RelaySender? _relay;

    public SendCoordinator(TransferQueue queue, StateStore store, RelaySender? relay, params ITransport[] transports)
    {
        _queue = queue;
        _store = store;
        _relay = relay;
        _transports = transports.ToDictionary(t => t.Kind);
        _queue.Changed += () => { if (_wake.CurrentCount == 0) _wake.Release(); };
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public bool IsReachable(string deviceId) => _reachable.TryGetValue(deviceId, out bool r) && r;

    /// <summary>Probe all devices (used for tray status and reconnect detection).</summary>
    public async Task RefreshReachabilityAsync()
    {
        var devices = _store.State.Devices.ToList();
        foreach (var device in devices)
        {
            if (!_transports.TryGetValue(device.Kind, out var transport)) continue;
            bool wasReachable = IsReachable(device.Id);
            bool now = await transport.ProbeAsync(device, _cts.Token).ConfigureAwait(false);
            _reachable[device.Id] = now;
            if (now && !wasReachable)
            {
                Logger.Info($"{device.Name} came online — flushing queue");
                _queue.ResetBackoff(device.Id);
            }
        }
        ReachabilityChanged?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Error("Send loop error", ex);
            }

            var nextDue = _queue.NextDueAt();
            TimeSpan wait = nextDue == null
                ? Timeout.InfiniteTimeSpan
                : Clamp(nextDue.Value - DateTime.UtcNow, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30));
            try { await _wake.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Cheap presence check while work is pending, so reconnects flush immediately.
            if (_queue.PendingCount > 0)
                await RefreshReachabilityAsync().ConfigureAwait(false);
        }
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        foreach (var device in _store.State.Devices.ToList())
        {
            if (!_transports.TryGetValue(device.Kind, out var transport)) continue;

            foreach (var item in _queue.Due(device.Id))
            {
                var progress = new Progress<long>(sent =>
                    Progress?.Invoke(new TransferProgress(item.Id, item.Name, device.Name, sent, item.Size, false, false)));
                try
                {
                    await transport.SendAsync(device, item, _queue.PayloadPath(item.FileId), progress, ct).ConfigureAwait(false);
                    _queue.MarkSent(item);
                    _reachable[device.Id] = true;
                    _store.AddRecent(item.Name, device.Name, "sent");
                    Progress?.Invoke(new TransferProgress(item.Id, item.Name, device.Name, item.Size, item.Size, true, false));
                    Logger.Info($"Sent {item.Name} ({item.Size / 1024} KB) to {device.Name}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _reachable[device.Id] = false;
                    Logger.Error($"LAN send of {item.Name} to {device.Name} failed (attempt {item.Attempts})", ex);

                    // Phone not on the LAN — fall back to the E2EE relay + push path.
                    if (_relay != null && _relay.CanSendTo(device))
                    {
                        try
                        {
                            await _relay.SendAsync(device, item, _queue.PayloadPath(item.FileId), ct).ConfigureAwait(false);
                            _queue.MarkSent(item);
                            _store.AddRecent(item.Name, device.Name, "sent via relay");
                            Progress?.Invoke(new TransferProgress(item.Id, item.Name, device.Name, item.Size, item.Size, true, false));
                            Logger.Info($"Sent {item.Name} to {device.Name} via relay");
                            continue;
                        }
                        catch (Exception relayEx)
                        {
                            Logger.Error($"Relay send of {item.Name} failed", relayEx);
                        }
                    }

                    _queue.MarkFailed(item);
                    Progress?.Invoke(new TransferProgress(item.Id, item.Name, device.Name, 0, item.Size, false, true));
                    break; // device likely offline — stop hammering it, backoff will retry
                }
            }
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
