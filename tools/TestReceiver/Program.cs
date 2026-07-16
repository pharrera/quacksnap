using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using QuackSnap.Protocol;

// Development stand-in for the iPhone app: pairs with the Windows sender and
// receives files over the same mutually-authenticated TLS protocol.
//
//   dotnet run -- pair "quacksnap://pair?..."   one-time pairing
//   dotnet run -- listen                          receive into ./received
//
// State (identity + pinned peer) lives in ./receiver-data. Plaintext on disk —
// this is a dev tool, not a product.

const int DefaultListenPort = 47820;
const string PfxPassword = "quacksnap-dev";

string dataDir = Path.Combine(Environment.CurrentDirectory, "receiver-data");
Directory.CreateDirectory(dataDir);
string statePath = Path.Combine(dataDir, "state.json");
string pfxPath = Path.Combine(dataDir, "identity.pfx");
string receivedDir = Path.Combine(Environment.CurrentDirectory, "received");
Directory.CreateDirectory(receivedDir);

var identity = LoadOrCreateIdentity();
var state = LoadState();

switch (args.FirstOrDefault() ?? "listen")
{
    case "pair" when args.Length >= 2:
        await PairAsync(args[1]);
        break;
    case "listen":
        await ListenAsync();
        break;
    default:
        Console.WriteLine("Usage: TestReceiver pair \"<quacksnap://pair?...>\" | TestReceiver listen");
        return 1;
}
return 0;

async Task PairAsync(string uri)
{
    var payload = PairingPayload.Parse(uri);
    Console.WriteLine($"Pairing with {payload.Name} ({string.Join(", ", payload.Hosts)}:{payload.Port})…");

    foreach (var host in payload.Hosts)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, payload.Port, cts.Token);
            var stream = client.GetStream();

            var request = new PairRequest(1, state.DeviceId, state.Name, state.ListenPort,
                CertUtil.Fingerprint(identity), Mac: "");
            request = request with { Mac = PairingMac.ForRequest(payload.Secret, request) };
            await Frames.WriteAsync(stream, FrameType.Hello, Json.Serialize(request), cts.Token);

            var (_, respPayload) = await Frames.ReadAsync(stream, cts.Token);
            var response = Json.Deserialize<PairResponse>(respPayload);
            if (!response.Ok)
                throw new Exception($"Sender rejected pairing: {response.Error}");
            string expected = PairingMac.ForResponse(payload.Secret, response.DeviceId!, response.Name!, response.CertFp!);
            if (!PairingMac.Verify(expected, response.Mac!))
                throw new Exception("Sender failed MAC verification — wrong code or tampering");
            if (response.CertFp != payload.CertFp)
                throw new Exception("Sender certificate does not match the QR payload");

            state.Peer = new PeerInfo(response.DeviceId!, response.Name!, response.CertFp!);
            SaveState();
            Console.WriteLine($"Paired with {response.Name}. Now run: dotnet run -- listen");
            return;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            Console.WriteLine($"  {host} unreachable, trying next…");
        }
    }
    Console.WriteLine("Could not reach the sender on any advertised address.");
}

async Task ListenAsync()
{
    if (state.Peer == null)
    {
        Console.WriteLine("Not paired yet. Run: dotnet run -- pair \"<quacksnap://...>\"");
        return;
    }

    var listener = new TcpListener(IPAddress.Any, state.ListenPort);
    listener.Start();
    Console.WriteLine($"Listening on port {state.ListenPort} as \"{state.Name}\"; saving to {receivedDir}");
    Console.WriteLine($"Paired sender: {state.Peer.Name} (fp {state.Peer.CertFp[..12]}…)");

    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(() => HandleConnectionAsync(client));
    }
}

async Task HandleConnectionAsync(TcpClient client)
{
    var remote = client.Client.RemoteEndPoint;
    try
    {
        using (client)
        {
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c && CertUtil.Fingerprint(c) == state.Peer!.CertFp,
            });

            var (type, payload) = await Frames.ReadAsync(ssl, CancellationToken.None);
            if (type != FrameType.Hello) throw new InvalidDataException($"Expected Hello, got {type}");
            var hello = Json.Deserialize<HelloMessage>(payload);
            Console.WriteLine($"[{remote}] session from {hello.DeviceName}");
            await Frames.WriteAsync(ssl, FrameType.Hello,
                Json.Serialize(new HelloMessage(1, state.DeviceId, state.Name)), CancellationToken.None);

            await ReceiveLoopAsync(ssl);
        }
    }
    catch (EndOfStreamException)
    {
        Console.WriteLine($"[{remote}] session closed");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{remote}] error: {ex.Message}");
    }
}

async Task ReceiveLoopAsync(SslStream ssl)
{
    OfferMessage? current = null;
    FileStream? file = null;
    string partPath = "";

    try
    {
        while (true)
        {
            var (type, payload) = await Frames.ReadAsync(ssl, CancellationToken.None);
            switch (type)
            {
                case FrameType.Ping:
                    await Frames.WriteAsync(ssl, FrameType.Pong, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
                    break;

                case FrameType.Offer:
                    current = Json.Deserialize<OfferMessage>(payload);
                    partPath = Path.Combine(receivedDir, current.FileId + ".part");
                    long offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    file = new FileStream(partPath, FileMode.Append, FileAccess.Write);
                    Console.WriteLine($"  ← {current.Name} ({current.Size / 1024} KB), resuming at {offset}");
                    await Frames.WriteAsync(ssl, FrameType.Accept,
                        Json.Serialize(new AcceptMessage(current.FileId, offset)), CancellationToken.None);
                    break;

                case FrameType.Chunk:
                    if (current == null || file == null) throw new InvalidDataException("Chunk without an offer");
                    var (fileId, chunkOffset, data) = ChunkFrame.Parse(payload);
                    if (Convert.ToHexString(fileId).ToLowerInvariant() != current.FileId)
                        throw new InvalidDataException("Chunk for unexpected file");
                    if (chunkOffset != file.Length)
                        throw new InvalidDataException($"Out-of-order chunk at {chunkOffset}, have {file.Length}");
                    await file.WriteAsync(data);
                    break;

                case FrameType.Done:
                    if (current == null || file == null) throw new InvalidDataException("Done without an offer");
                    await file.FlushAsync();
                    file.Dispose();
                    file = null;

                    string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(partPath))).ToLowerInvariant();
                    if (actual != current.FileId)
                    {
                        File.Delete(partPath);
                        await Frames.WriteAsync(ssl, FrameType.Ack,
                            Json.Serialize(new AckMessage(current.FileId, false, "Hash mismatch")), CancellationToken.None);
                        Console.WriteLine($"  ✗ {current.Name}: hash mismatch, discarded");
                    }
                    else
                    {
                        string finalPath = UniquePath(Path.Combine(receivedDir, SafeName(current.Name)));
                        File.Move(partPath, finalPath, overwrite: false);
                        await Frames.WriteAsync(ssl, FrameType.Ack,
                            Json.Serialize(new AckMessage(current.FileId, true)), CancellationToken.None);
                        Console.WriteLine($"  ✓ saved {finalPath}");
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
        file?.Dispose();
    }
}

X509Certificate2 LoadOrCreateIdentity()
{
    if (File.Exists(pfxPath))
        return CertUtil.LoadPfx(File.ReadAllBytes(pfxPath), PfxPassword);
    var cert = CertUtil.CreateIdentity(Environment.MachineName + "-receiver");
    File.WriteAllBytes(pfxPath, CertUtil.ExportPfx(cert, PfxPassword));
    return CertUtil.LoadPfx(File.ReadAllBytes(pfxPath), PfxPassword);
}

ReceiverState LoadState()
{
    if (File.Exists(statePath))
        return JsonSerializer.Deserialize<ReceiverState>(File.ReadAllText(statePath), Json.Options) ?? NewState();
    var s = NewState();
    File.WriteAllText(statePath, JsonSerializer.Serialize(s, Json.Options));
    return s;

    static ReceiverState NewState() => new()
    {
        DeviceId = Guid.NewGuid().ToString("N"),
        Name = Environment.MachineName + " (test receiver)",
        ListenPort = DefaultListenPort,
    };
}

void SaveState() => File.WriteAllText(statePath, JsonSerializer.Serialize(state, Json.Options));

static string SafeName(string name)
{
    foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    return name;
}

static string UniquePath(string path)
{
    if (!File.Exists(path)) return path;
    string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
    for (int i = 2; ; i++)
    {
        string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
        if (!File.Exists(candidate)) return candidate;
    }
}

sealed class ReceiverState
{
    public required string DeviceId { get; set; }
    public required string Name { get; set; }
    public int ListenPort { get; set; } = 47820;
    public PeerInfo? Peer { get; set; }
}

sealed record PeerInfo(string DeviceId, string Name, string CertFp);
