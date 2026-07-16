using System.Security.Cryptography;
using System.Text;

namespace QuackSnap.Protocol;

public static class Discovery
{
    /// <summary>DNS-SD service type advertised while a pairing window is open.</summary>
    public const string PairingServiceType = "_quacksnap-pair._tcp";

    /// <summary>DNS-SD service type a receiver advertises so paired devices can send
    /// files to it (e.g. iPhone → Windows). TXT record carries id=&lt;deviceId&gt;.</summary>
    public const string TransferServiceType = "_quacksnap._tcp";
}

/// <summary>
/// LocalSend-style short pairing code. Typed instead of the full quacksnap:// URI;
/// discovery (Bonjour) supplies the endpoint, the code supplies the HMAC secret.
/// Low entropy is compensated by: listener exists only while the pairing window is
/// open, a fresh code per session, and a hard limit on failed attempts.
/// </summary>
public static class PairingCode
{
    public static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>"483 921", "483-921", "483921" all normalize to the same secret.</summary>
    public static string Normalize(string code) =>
        new(code.Where(char.IsAsciiDigit).ToArray());

    public static byte[] ToSecret(string code) => Encoding.UTF8.GetBytes(Normalize(code));

    public static string Display(string code) =>
        code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
}
