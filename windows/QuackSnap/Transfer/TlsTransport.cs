using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using QuackSnap.Core;
using QuackSnap.Protocol;

namespace QuackSnap.Transfer;

/// <summary>
/// Direct transfer to a paired application over mutually authenticated TLS 1.2/1.3.
/// Both ends present self-signed certs and validate nothing except the SHA-256
/// fingerprint pinned at pairing time. Sessions are cached warm between sends.
/// </summary>
public sealed class TlsTransport : ITransport, IDisposable
{
    private readonly Identity _identity;
    private readonly Dictionary<string, CachedSession> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Kind => DeviceKind.Application;

    public TlsTransport(Identity identity) => _identity = identity;

    public async Task<bool> ProbeAsync(Device device, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await client.ConnectAsync(device.Host, device.Port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task SendAsync(Device device, TransferItem item, string payloadPath, IProgress<long>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = await GetSessionAsync(device, ct).ConfigureAwait(false);
            try
            {
                await SendOverSessionAsync(session, item, payloadPath, progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                // Stale cached session — reconnect once and retry.
                DropSession(device.Id);
                session = await GetSessionAsync(device, ct).ConfigureAwait(false);
                await SendOverSessionAsync(session, item, payloadPath, progress, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task SendOverSessionAsync(CachedSession session, TransferItem item, string payloadPath, IProgress<long>? progress, CancellationToken ct)
    {
        var stream = session.Stream;
        byte[] fileIdRaw = Convert.FromHexString(item.FileId);

        await Frames.WriteAsync(stream, FrameType.Offer,
            Json.Serialize(new OfferMessage(item.FileId, item.Name, item.Mime, item.Size,
                new DateTimeOffset(item.CreatedAt).ToUnixTimeMilliseconds())), ct).ConfigureAwait(false);

        var (type, payload) = await Frames.ReadAsync(stream, ct).ConfigureAwait(false);
        if (type == FrameType.Error)
            throw new IOException($"Receiver error: {Json.Deserialize<ErrorMessage>(payload).Message}");
        if (type != FrameType.Accept)
            throw new InvalidDataException($"Expected Accept, got {type}");
        long offset = Json.Deserialize<AcceptMessage>(payload).Offset;

        if (offset < item.Size)
        {
            await using var file = File.OpenRead(payloadPath);
            file.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[Frames.ChunkSize];
            long sent = offset;
            int n;
            while ((n = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await Frames.WriteAsync(stream, FrameType.Chunk,
                    ChunkFrame.Build(fileIdRaw, sent, buffer.AsSpan(0, n)), ct).ConfigureAwait(false);
                sent += n;
                progress?.Report(sent);
            }
        }

        await Frames.WriteAsync(stream, FrameType.Done, Json.Serialize(new DoneMessage(item.FileId)), ct).ConfigureAwait(false);

        (type, payload) = await Frames.ReadAsync(stream, ct).ConfigureAwait(false);
        if (type != FrameType.Ack)
            throw new InvalidDataException($"Expected Ack, got {type}");
        var ack = Json.Deserialize<AckMessage>(payload);
        if (!ack.Ok)
            throw new IOException($"Receiver rejected file: {ack.Error}");
        session.LastUsed = DateTime.UtcNow;
    }

    private async Task<CachedSession> GetSessionAsync(Device device, CancellationToken ct)
    {
        if (_sessions.TryGetValue(device.Id, out var cached))
        {
            if (DateTime.UtcNow - cached.LastUsed < TimeSpan.FromSeconds(60) && cached.Client.Connected)
                return cached;
            DropSession(device.Id);
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(device.Host, device.Port, timeout.Token).ConfigureAwait(false);

            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "quacksnap",
                ClientCertificates = new X509CertificateCollection { _identity.Certificate },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c && CertUtil.Fingerprint(c) == device.CertFingerprint,
            }, ct).ConfigureAwait(false);

            await Frames.WriteAsync(ssl, FrameType.Hello,
                Json.Serialize(new HelloMessage(1, _identity.DeviceId, _identity.DeviceName)), ct).ConfigureAwait(false);
            var (type, payload) = await Frames.ReadAsync(ssl, ct).ConfigureAwait(false);
            if (type != FrameType.Hello)
                throw new InvalidDataException($"Expected Hello, got {type}");
            var hello = Json.Deserialize<HelloMessage>(payload);
            if (hello.DeviceId != device.Id)
                throw new InvalidDataException("Peer device id does not match the paired device");

            var session = new CachedSession(client, ssl) { LastUsed = DateTime.UtcNow };
            _sessions[device.Id] = session;
            Logger.Info($"TLS session established with {device.Name} ({device.Host}:{device.Port})");
            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void DropSession(string deviceId)
    {
        if (_sessions.Remove(deviceId, out var session))
            session.Dispose();
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
    }

    private sealed class CachedSession(TcpClient client, SslStream stream) : IDisposable
    {
        public TcpClient Client { get; } = client;
        public SslStream Stream { get; } = stream;
        public DateTime LastUsed { get; set; }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }
}
