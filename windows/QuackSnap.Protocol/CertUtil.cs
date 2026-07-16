using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace QuackSnap.Protocol;

public static class CertUtil
{
    /// <summary>
    /// Self-signed ECDSA P-256 identity certificate. Trust is established by pinning
    /// the fingerprint at pairing time, so validity dates and CN are informational.
    /// </summary>
    public static X509Certificate2 CreateIdentity(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=QuackSnap {name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1"), // server auth
                new Oid("1.3.6.1.5.5.7.3.2"), // client auth
            },
            critical: false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
    }

    /// <summary>SHA-256 of the DER-encoded certificate, base64url — the pinned identity.</summary>
    public static string Fingerprint(X509Certificate2 cert) =>
        Base64Url.Encode(SHA256.HashData(cert.RawData));

    public static byte[] ExportPfx(X509Certificate2 cert, string password) =>
        cert.Export(X509ContentType.Pfx, password);

    public static X509Certificate2 LoadPfx(byte[] pfx, string password) =>
        new(pfx, password, X509KeyStorageFlags.Exportable);
}
