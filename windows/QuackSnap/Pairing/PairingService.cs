using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QuackSnap.Core;
using QuackSnap.Protocol;

namespace QuackSnap.Pairing;

/// <summary>
/// Runs only while the pairing window is open. Listens on an ephemeral TCP port
/// and advertises it over Bonjour so the phone finds us automatically. Two ways
/// to prove possession of the out-of-band secret, both via HMAC:
///  - the QR/URI payload (128-bit secret), or
///  - the short 6-digit code shown in the window (LocalSend-style; low entropy is
///    offset by the session-only listener and a hard failed-attempt limit).
/// </summary>
public sealed class PairingService : IDisposable
{
    private const int MaxFailedAttempts = 3;

    private readonly Identity _identity;
    private readonly TcpListener _listener;
    private readonly byte[] _qrSecret = PairingPayload.NewSecret();
    private readonly byte[] _codeSecret;
    private readonly CancellationTokenSource _cts = new();
    private readonly BonjourAdvertiser? _advertiser;
    private int _failedAttempts;

    public PairingPayload Payload { get; }
    public string Code { get; } = PairingCode.NewCode();

    /// <summary>Fires on a threadpool thread when a device pairs successfully.</summary>
    public event Action<Device>? Paired;

    /// <summary>Too many wrong codes — the session is dead; close and reopen to retry.</summary>
    public event Action? AttemptsExceeded;

    private readonly string? _relayUrl;

    public PairingService(Identity identity, string? relayUrl = null)
    {
        _identity = identity;
        _relayUrl = relayUrl;
        _codeSecret = PairingCode.ToSecret(Code);
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Payload = new PairingPayload(LocalAddresses(), port, _qrSecret, identity.Fingerprint, identity.DeviceName, identity.DeviceId);

        try
        {
            _advertiser = new BonjourAdvertiser(
                $"QuackSnap {identity.DeviceName}", Discovery.PairingServiceType, port,
                new Dictionary<string, string> { ["name"] = identity.DeviceName, ["id"] = identity.DeviceId });
        }
        catch (Exception ex)
        {
            // Discovery is best-effort; QR and pasted-URI pairing still work without it.
            Logger.Error("Bonjour advertising failed — code pairing needs discovery, use QR instead", ex);
        }

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Logger.Info($"Pairing listener on port {port}, code {Code}");
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

                byte[]? provenSecret = null;
                foreach (var secret in new[] { _qrSecret, _codeSecret })
                {
                    if (PairingMac.Verify(PairingMac.ForRequest(secret, request), request.Mac))
                    {
                        provenSecret = secret;
                        break;
                    }
                }

                if (provenSecret == null)
                {
                    await Frames.WriteAsync(stream, FrameType.Hello,
                        Json.Serialize(new PairResponse(false, null, null, null, null, "Wrong pairing code")), timeout.Token).ConfigureAwait(false);
                    Logger.Error("Pairing attempt with invalid MAC rejected");
                    if (Interlocked.Increment(ref _failedAttempts) >= MaxFailedAttempts)
                    {
                        Logger.Error("Too many failed pairing attempts — closing session");
                        _listener.Stop();
                        AttemptsExceeded?.Invoke();
                    }
                    return;
                }

                // The full cert must hash to the MAC-covered fingerprint before we keep it.
                string? certDer = request.CertDer;
                if (certDer != null)
                {
                    var der = Convert.FromBase64String(certDer);
                    if (Base64Url.Encode(System.Security.Cryptography.SHA256.HashData(der)) != request.CertFp)
                    {
                        Logger.Error("Pairing certDer does not match fingerprint — dropping cert");
                        certDer = null;
                    }
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
                    CertDer = certDer,
                };

                var response = new PairResponse(true, _identity.DeviceId, _identity.DeviceName, _identity.Fingerprint,
                    PairingMac.ForResponse(provenSecret, _identity.DeviceId, _identity.DeviceName, _identity.Fingerprint),
                    CertDer: Convert.ToBase64String(_identity.Certificate.RawData),
                    RelayUrl: _relayUrl);
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
        _advertiser?.Dispose();
        _listener.Stop();
        _cts.Dispose();
    }
}
