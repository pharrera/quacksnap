using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuackSnap.Protocol;

// Development stand-in for the Windows sender: hosts a pairing window and sends
// files to a paired device (the iOS app or TestReceiver) over the same mutually
// authenticated TLS protocol. Lets the whole pipeline be exercised on any OS.
//
//   dotnet run -- pairing-host                open a pairing window (code + QR + Bonjour)
//   dotnet run -- send <file> [file2 ...]     send file(s) to the paired device
//   dotnet run -- receive                     stand in for Windows: advertise
//                                             _quacksnap._tcp and receive files
//
// State (identity + paired peer) lives in ./sender-data. Dev tool, not a product.

const string PfxPassword = "quacksnap-dev";
string dataDir = Path.Combine(Environment.CurrentDirectory, "sender-data");
Directory.CreateDirectory(dataDir);
string pfxPath = Path.Combine(dataDir, "identity.pfx");
string peerPath = Path.Combine(dataDir, "peer.txt");
string peerCertPath = Path.Combine(dataDir, "peer-cert.der");

var identity = LoadOrCreateIdentity();

switch (args.FirstOrDefault())
{
    case "pairing-host":
        await PairingHostAsync();
        break;
    case "send" when args.Length >= 2:
        foreach (var file in args.Skip(1))
            await SendAsync(file);
        break;
    case "receive":
        await ReceiveAsync();
        break;
    default:
        Console.WriteLine("Usage: DevSender pairing-host | DevSender send <file> [...] | DevSender receive");
        return 1;
}
return 0;

async Task ReceiveAsync()
{
    if (!File.Exists(peerPath))
    {
        Console.WriteLine("Not paired. Run: DevSender pairing-host");
        return;
    }
    string peerFp = File.ReadAllLines(peerPath)[1];
    string receiveDir = Path.Combine(Environment.CurrentDirectory, "received-from-phone");
    Directory.CreateDirectory(receiveDir);

    var listener = new TcpListener(IPAddress.Any, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var advertiser = new BonjourAdvertiser("QuackSnap DevSender", Discovery.TransferServiceType, port,
        new Dictionary<string, string> { ["id"] = "sender-device-1", ["name"] = "DevSender" });
    Console.WriteLine($"Advertising {Discovery.TransferServiceType} on port {port}; saving to {receiveDir}");

    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            using (client)
            {
                try
                {
                    var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = identity,
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = (_, cert, _, _) =>
                            cert is X509Certificate2 c && CertUtil.Fingerprint(c) == peerFp,
                    });
                    var (t, p) = await Frames.ReadAsync(ssl, CancellationToken.None);
                    var hello = Json.Deserialize<HelloMessage>(p);
                    Console.WriteLine($"session from {hello.DeviceName}");
                    await Frames.WriteAsync(ssl, FrameType.Hello,
                        Json.Serialize(new HelloMessage(1, "sender-device-1", "DevSender")), CancellationToken.None);
                    await ReceiveLoopAsync(ssl, receiveDir);
                }
                catch (Exception ex) { Console.WriteLine($"error: {ex.Message}"); }
            }
        });
    }
}

static async Task ReceiveLoopAsync(SslStream ssl, string dir)
{
    OfferMessage? current = null;
    FileStream? file = null;
    string partPath = "";
    while (true)
    {
        var (type, payload) = await Frames.ReadAsync(ssl, CancellationToken.None);
        switch (type)
        {
            case FrameType.Offer:
                current = Json.Deserialize<OfferMessage>(payload);
                partPath = Path.Combine(dir, current.FileId + ".part");
                long offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                file = new FileStream(partPath, FileMode.Append, FileAccess.Write);
                await Frames.WriteAsync(ssl, FrameType.Accept,
                    Json.Serialize(new AcceptMessage(current.FileId, offset)), CancellationToken.None);
                break;
            case FrameType.Chunk:
                var (_, chunkOffset, data) = ChunkFrame.Parse(payload);
                await file!.WriteAsync(data);
                break;
            case FrameType.Done:
                await file!.FlushAsync(); file.Dispose(); file = null;
                string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(partPath))).ToLowerInvariant();
                bool ok = actual == current!.FileId;
                if (ok)
                {
                    string final = Path.Combine(dir, current.Name);
                    File.Move(partPath, final, overwrite: true);
                    Console.WriteLine($"  ✓ received {current.Name} ({current.Size} bytes)");
                }
                else { File.Delete(partPath); Console.WriteLine($"  ✗ {current.Name} hash mismatch"); }
                await Frames.WriteAsync(ssl, FrameType.Ack,
                    Json.Serialize(new AckMessage(current.FileId, ok, ok ? null : "hash mismatch")), CancellationToken.None);
                current = null;
                break;
            default:
                return;
        }
    }
}

async Task PairingHostAsync()
{
    var qrSecret = PairingPayload.NewSecret();
    string code = PairingCode.NewCode();
    byte[] codeSecret = PairingCode.ToSecret(code);

    var listener = new TcpListener(IPAddress.Any, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var payload = new PairingPayload(new[] { "127.0.0.1" }, port, qrSecret,
        CertUtil.Fingerprint(identity), "DevSender", "sender-device-1");

    Console.WriteLine("URI:" + payload.ToUri());
    Console.WriteLine("CODE:" + code);

    using var advertiser = new BonjourAdvertiser("QuackSnap DevSender", Discovery.PairingServiceType, port,
        new Dictionary<string, string> { ["name"] = "DevSender", ["id"] = "sender-device-1" });
    Console.WriteLine($"Advertising {Discovery.PairingServiceType} on port {port}. Waiting for a device…");

    using var client = await listener.AcceptTcpClientAsync();
    var stream = client.GetStream();
    var (type, reqPayload) = await Frames.ReadAsync(stream, CancellationToken.None);
    if (type != FrameType.Hello) throw new Exception("expected pairing Hello");
    var request = Json.Deserialize<PairRequest>(reqPayload);

    byte[]? proven = new[] { qrSecret, codeSecret }
        .FirstOrDefault(s => PairingMac.Verify(PairingMac.ForRequest(s, request), request.Mac));
    if (proven == null)
    {
        await Frames.WriteAsync(stream, FrameType.Hello,
            Json.Serialize(new PairResponse(false, null, null, null, null, "Wrong code")), CancellationToken.None);
        Console.WriteLine("REJECTED (bad code)");
        return;
    }

    var response = new PairResponse(true, "sender-device-1", "DevSender", CertUtil.Fingerprint(identity),
        PairingMac.ForResponse(proven, "sender-device-1", "DevSender", CertUtil.Fingerprint(identity)),
        CertDer: Convert.ToBase64String(identity.RawData));
    await Frames.WriteAsync(stream, FrameType.Hello, Json.Serialize(response), CancellationToken.None);

    File.WriteAllText(peerPath, $"{request.ListenPort}\n{request.CertFp}\n{request.DeviceId}");
    if (request.CertDer != null)
        File.WriteAllBytes(peerCertPath, Convert.FromBase64String(request.CertDer));
    Console.WriteLine($"PAIRED name={request.Name} port={request.ListenPort} fp={request.CertFp}");
}

async Task SendAsync(string file)
{
    if (!File.Exists(peerPath))
    {
        Console.WriteLine("Not paired. Run: DevSender pairing-host");
        return;
    }
    var lines = File.ReadAllLines(peerPath);
    int port = int.Parse(lines[0]);
    string peerFp = lines[1];

    byte[] bytes = await File.ReadAllBytesAsync(file);
    string fileId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    byte[] fileIdRaw = Convert.FromHexString(fileId);
    string mime = MimeFor(file);

    using var tcp = new TcpClient { NoDelay = true };
    await tcp.ConnectAsync("127.0.0.1", port);
    var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost = "quacksnap",
        ClientCertificates = new X509CertificateCollection { identity },
        RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is X509Certificate2 c && CertUtil.Fingerprint(c) == peerFp,
    });

    await Frames.WriteAsync(ssl, FrameType.Hello,
        Json.Serialize(new HelloMessage(1, "sender-device-1", "DevSender")), CancellationToken.None);
    _ = await Frames.ReadAsync(ssl, CancellationToken.None);

    await Frames.WriteAsync(ssl, FrameType.Offer, Json.Serialize(new OfferMessage(
        fileId, Path.GetFileName(file), mime, bytes.Length,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), CancellationToken.None);
    var (_, acceptPayload) = await Frames.ReadAsync(ssl, CancellationToken.None);
    long offset = Json.Deserialize<AcceptMessage>(acceptPayload).Offset;

    for (long pos = offset; pos < bytes.Length; pos += Frames.ChunkSize)
    {
        int n = (int)Math.Min(Frames.ChunkSize, bytes.Length - pos);
        await Frames.WriteAsync(ssl, FrameType.Chunk,
            ChunkFrame.Build(fileIdRaw, pos, bytes.AsSpan((int)pos, n)), CancellationToken.None);
    }
    await Frames.WriteAsync(ssl, FrameType.Done, Json.Serialize(new DoneMessage(fileId)), CancellationToken.None);
    var (_, ackPayload) = await Frames.ReadAsync(ssl, CancellationToken.None);
    var ack = Json.Deserialize<AckMessage>(ackPayload);
    Console.WriteLine(ack.Ok ? $"SENT {Path.GetFileName(file)} ({bytes.Length} bytes, {mime})" : $"FAILED: {ack.Error}");
}

static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".pdf" => "application/pdf",
    ".txt" => "text/plain",
    ".md" => "text/markdown",
    ".json" => "application/json",
    _ => "application/octet-stream",
};

X509Certificate2 LoadOrCreateIdentity()
{
    if (File.Exists(pfxPath))
        return CertUtil.LoadPfx(File.ReadAllBytes(pfxPath), PfxPassword);
    var cert = CertUtil.CreateIdentity("DevSender");
    File.WriteAllBytes(pfxPath, CertUtil.ExportPfx(cert, PfxPassword));
    return CertUtil.LoadPfx(File.ReadAllBytes(pfxPath), PfxPassword);
}
