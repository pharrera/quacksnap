using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuackSnap.Core;
using QuackSnap.Protocol;

namespace QuackSnap.Transfer;

public sealed record ReceivedFile(string Path, string Name, long Size, string From);

/// <summary>
/// Inbound side of the reverse direction (iPhone → Windows). A mutually
/// authenticated TLS server that accepts connections from any paired device
/// (pinning that device's certificate) and saves files into the receive folder.
/// Advertises <c>_quacksnap._tcp</c> so paired phones can discover this PC.
/// </summary>
public sealed class ReceiveServer : IDisposable
{
    private readonly Identity _identity;
    private readonly StateStore _store;
    private readonly string _receiveDir;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private BonjourAdvertiser? _advertiser;

    public event Action<ReceivedFile>? FileReceived;

    public string ReceiveDirectory => _receiveDir;

    public ReceiveServer(Identity identity, StateStore store)
    {
        _identity = identity;
        _store = store;
        _receiveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "QuackSnap");
        Directory.CreateDirectory(_receiveDir);
        _listener = new TcpListener(IPAddress.Any, 0);
    }

    public void Start()
    {
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        try
        {
            _advertiser = new BonjourAdvertiser(
                $"QuackSnap {_identity.DeviceName}", Discovery.TransferServiceType, port,
                new Dictionary<string, string> { ["id"] = _identity.DeviceId, ["name"] = _identity.DeviceName });
        }
        catch (Exception ex)
        {
            Logger.Error("Advertising receive service failed — phone won't discover this PC", ex);
        }
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Logger.Info($"Receive server listening on port {port}, saving to {_receiveDir}");
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
                var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity.Certificate,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        cert is X509Certificate2 c && MatchesPairedDevice(CertUtil.Fingerprint(c)),
                }, ct).ConfigureAwait(false);

                var (type, payload) = await Frames.ReadAsync(ssl, ct).ConfigureAwait(false);
                if (type != FrameType.Hello) throw new InvalidDataException($"Expected Hello, got {type}");
                var hello = Json.Deserialize<HelloMessage>(payload);
                string from = _store.State.Devices.FirstOrDefault(d => d.Id == hello.DeviceId)?.Name ?? hello.DeviceName;
                await Frames.WriteAsync(ssl, FrameType.Hello,
                    Json.Serialize(new HelloMessage(1, _identity.DeviceId, _identity.DeviceName)), ct).ConfigureAwait(false);

                await ReceiveLoopAsync(ssl, from, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EndOfStreamException or OperationCanceledException)
            {
                // Peer closed / cancelled — normal.
            }
            catch (Exception ex)
            {
                Logger.Error("Inbound transfer failed", ex);
            }
        }
    }

    private bool MatchesPairedDevice(string fingerprint) =>
        _store.State.Devices.Any(d => d.CertFingerprint == fingerprint);

    private async Task ReceiveLoopAsync(SslStream ssl, string from, CancellationToken ct)
    {
        OfferMessage? current = null;
        FileStream? file = null;
        string partPath = "";

        try
        {
            while (true)
            {
                var (type, payload) = await Frames.ReadAsync(ssl, ct).ConfigureAwait(false);
                switch (type)
                {
                    case FrameType.Ping:
                        await Frames.WriteAsync(ssl, FrameType.Pong, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                        break;

                    case FrameType.Offer:
                        current = Json.Deserialize<OfferMessage>(payload);
                        partPath = Path.Combine(_receiveDir, current.FileId + ".part");
                        long offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                        file = new FileStream(partPath, FileMode.Append, FileAccess.Write);
                        await Frames.WriteAsync(ssl, FrameType.Accept,
                            Json.Serialize(new AcceptMessage(current.FileId, offset)), ct).ConfigureAwait(false);
                        break;

                    case FrameType.Chunk:
                        if (current == null || file == null) throw new InvalidDataException("Chunk without an offer");
                        var (fileId, chunkOffset, data) = ChunkFrame.Parse(payload);
                        if (Convert.ToHexString(fileId).ToLowerInvariant() != current.FileId)
                            throw new InvalidDataException("Chunk for unexpected file");
                        if (chunkOffset != file.Length)
                            throw new InvalidDataException($"Out-of-order chunk at {chunkOffset}, have {file.Length}");
                        await file.WriteAsync(data, ct).ConfigureAwait(false);
                        break;

                    case FrameType.Done:
                        if (current == null || file == null) throw new InvalidDataException("Done without an offer");
                        await file.FlushAsync(ct).ConfigureAwait(false);
                        await file.DisposeAsync().ConfigureAwait(false);
                        file = null;

                        string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(partPath, ct)
                            .ConfigureAwait(false))).ToLowerInvariant();
                        if (actual != current.FileId)
                        {
                            File.Delete(partPath);
                            await Frames.WriteAsync(ssl, FrameType.Ack,
                                Json.Serialize(new AckMessage(current.FileId, false, "Hash mismatch")), ct).ConfigureAwait(false);
                            Logger.Error($"Received {current.Name} failed hash check");
                        }
                        else
                        {
                            string finalPath = UniquePath(Path.Combine(_receiveDir, SafeName(current.Name)));
                            File.Move(partPath, finalPath);
                            await Frames.WriteAsync(ssl, FrameType.Ack,
                                Json.Serialize(new AckMessage(current.FileId, true)), ct).ConfigureAwait(false);
                            Logger.Info($"Received {current.Name} ({current.Size / 1024} KB) from {from}");
                            FileReceived?.Invoke(new ReceivedFile(finalPath, current.Name, current.Size, from));
                        }
                        current = null;
                        break;

                    default:
                        throw new InvalidDataException($"Unexpected frame {type}");
                }
            }
        }
        finally
        {
            if (file != null) await file.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string SafeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _advertiser?.Dispose();
        _listener.Stop();
        _cts.Dispose();
    }
}
