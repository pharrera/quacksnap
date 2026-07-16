using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using QuackSnap.Core;
using QuackSnap.Protocol;

namespace QuackSnap.Transfer;

/// <summary>
/// Fallback delivery when the phone is unreachable on the LAN: seal the file to
/// the phone's certificate, upload the ciphertext to the relay, and ask the relay
/// to send a push. The relay and Apple only ever see ciphertext; the phone's
/// notification extension decrypts and shows the file on the lock screen.
/// </summary>
public sealed class RelaySender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Identity _identity;
    private readonly Func<string?> _relayUrl;

    public RelaySender(Identity identity, Func<string?> relayUrl)
    {
        _identity = identity;
        _relayUrl = relayUrl;
    }

    public bool CanSendTo(Device device) =>
        !string.IsNullOrWhiteSpace(_relayUrl()) && device.CertDer != null;

    public async Task SendAsync(Device device, TransferItem item, string payloadPath, CancellationToken ct)
    {
        string relayUrl = _relayUrl()?.TrimEnd('/')
            ?? throw new InvalidOperationException("No relay configured");
        byte[] recipientCert = Convert.FromBase64String(device.CertDer
            ?? throw new InvalidOperationException("No certificate for device"));

        byte[] plaintext = await File.ReadAllBytesAsync(payloadPath, ct).ConfigureAwait(false);
        string blobId = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));

        var (envelope, ciphertext) = EnvelopeCrypto.Seal(
            plaintext, item.Name, item.Mime, recipientCert, _identity.Certificate, blobId, relayUrl);

        using (var upload = new HttpRequestMessage(HttpMethod.Put, $"{relayUrl}/v1/blob/{blobId}"))
        {
            upload.Content = new ByteArrayContent(ciphertext);
            using var response = await Http.SendAsync(upload, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        var push = new
        {
            to = device.Id,
            payload = new Dictionary<string, object>
            {
                ["aps"] = new Dictionary<string, object>
                {
                    ["alert"] = new { title = $"From {_identity.DeviceName}", body = item.Name },
                    ["mutable-content"] = 1,
                },
                ["qsEnvelope"] = JsonSerializer.SerializeToElement(envelope, Json.Options),
            },
        };
        using var pushResponse = await Http.PostAsJsonAsync($"{relayUrl}/v1/push", push, ct).ConfigureAwait(false);
        pushResponse.EnsureSuccessStatusCode();

        Logger.Info($"Relayed {item.Name} ({ciphertext.Length / 1024} KB) to {device.Name} via {relayUrl}");
    }
}
