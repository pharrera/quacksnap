using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace ScreenDrop.Protocol;

/// <summary>
/// The out-of-band pairing payload shown as a QR code / copyable string on the sender.
/// screendrop://pair?v=1&amp;h=host1,host2&amp;p=port&amp;s=secret&amp;fp=certFingerprint&amp;n=name&amp;id=deviceId
/// The secret never travels over the network; both sides prove knowledge of it via HMAC.
/// </summary>
public sealed record PairingPayload(string[] Hosts, int Port, byte[] Secret, string CertFp, string Name, string DeviceId)
{
    public const string Scheme = "screendrop";

    public string ToUri()
    {
        var sb = new StringBuilder($"{Scheme}://pair?v=1");
        sb.Append("&h=").Append(Uri.EscapeDataString(string.Join(',', Hosts)));
        sb.Append("&p=").Append(Port);
        sb.Append("&s=").Append(Base64Url.Encode(Secret));
        sb.Append("&fp=").Append(Uri.EscapeDataString(CertFp));
        sb.Append("&n=").Append(Uri.EscapeDataString(Name));
        sb.Append("&id=").Append(Uri.EscapeDataString(DeviceId));
        return sb.ToString();
    }

    public static PairingPayload Parse(string uri)
    {
        var parsed = new Uri(uri.Trim());
        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Not a {Scheme}:// URI");
        var q = HttpUtility.ParseQueryString(parsed.Query);
        string Get(string key) => q[key] ?? throw new FormatException($"Pairing URI missing '{key}'");
        return new PairingPayload(
            Hosts: Get("h").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Port: int.Parse(Get("p")),
            Secret: Base64Url.Decode(Get("s")),
            CertFp: Get("fp"),
            Name: Get("n"),
            DeviceId: Get("id"));
    }

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(16);
}

public static class PairingMac
{
    public static string ForRequest(byte[] secret, PairRequest req) =>
        Compute(secret, $"req|{req.DeviceId}|{req.Name}|{req.ListenPort}|{req.CertFp}");

    public static string ForResponse(byte[] secret, string deviceId, string name, string certFp) =>
        Compute(secret, $"resp|{deviceId}|{name}|{certFp}");

    public static bool Verify(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static string Compute(byte[] secret, string data) =>
        Base64Url.Encode(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(data)));
}

public static class Base64Url
{
    public static string Encode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }
}
