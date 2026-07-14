using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ScreenDrop.Core;
using ScreenDrop.Protocol;

namespace ScreenDrop.Pairing;

/// <summary>
/// Runs only while the pairing window is open. Listens on an ephemeral TCP port;
/// the payload (QR / copyable URI) carries a one-time secret, and both sides prove
/// knowledge of it with HMACs before exchanging certificate fingerprints.
/// </summary>
public sealed class PairingService : IDisposable
{
    private readonly Identity _identity;
    private readonly TcpListener _listener;
    private readonly byte[] _secret = PairingPayload.NewSecret();
    private readonly CancellationTokenSource _cts = new();

    public PairingPayload Payload { get; }

    /// <summary>Fires on a threadpool thread when a device pairs successfully.</summary>
    public event Action<Device>? Paired;

    public PairingService(Identity identity)
    {
        _identity = identity;
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Payload = new PairingPayload(LocalAddresses(), port, _secret, identity.Fingerprint, identity.DeviceName, identity.DeviceId);
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Logger.Info($"Pairing listener on port {port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();

                var (type, payload) = await Frames.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (type != FrameType.Hello) throw new InvalidDataException("Expected pairing Hello frame");
                var request = Json.Deserialize<PairRequest>(payload);

                string expected = PairingMac.ForRequest(_secret, request);
                if (!PairingMac.Verify(expected, request.Mac))
                {
                    await Frames.WriteAsync(stream, FrameType.Hello,
                        Json.Serialize(new PairResponse(false, null, null, null, null, "Bad pairing code")), timeout.Token).ConfigureAwait(false);
                    Logger.Error("Pairing attempt with invalid MAC rejected");
                    return;
                }

                var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                var device = new Device
                {
                    Id = request.DeviceId,
                    Name = request.Name,
                    Kind = DeviceKind.Application,
                    Host = remoteIp,
                    Port = request.ListenPort,
                    CertFingerprint = request.CertFp,
                };

                var response = new PairResponse(true, _identity.DeviceId, _identity.DeviceName, _identity.Fingerprint,
                    PairingMac.ForResponse(_secret, _identity.DeviceId, _identity.DeviceName, _identity.Fingerprint));
                await Frames.WriteAsync(stream, FrameType.Hello, Json.Serialize(response), timeout.Token).ConfigureAwait(false);

                Logger.Info($"Paired with {device.Name} at {device.Host}:{device.Port}");
                Paired?.Invoke(device);
            }
            catch (Exception ex)
            {
                Logger.Error("Pairing handshake failed", ex);
            }
        }
    }

    private static string[] LocalAddresses()
    {
        var addresses = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    addresses.Add(addr.Address.ToString());
            }
        }
        return addresses.Count > 0 ? addresses.ToArray() : new[] { "127.0.0.1" };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
