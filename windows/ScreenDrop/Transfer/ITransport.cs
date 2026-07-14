using ScreenDrop.Core;

namespace ScreenDrop.Transfer;

/// <summary>
/// Delivery seam. TlsTransport implements the "application" path (direct TLS to a
/// paired app). A future WebTransport will implement the "website" path behind the
/// same interface, so the queue and coordinator don't change.
/// </summary>
public interface ITransport
{
    string Kind { get; }

    Task<bool> ProbeAsync(Device device, CancellationToken ct);

    /// <summary>Sends one payload; throws on failure so the coordinator can back off.</summary>
    Task SendAsync(Device device, TransferItem item, string payloadPath, IProgress<long>? progress, CancellationToken ct);
}
