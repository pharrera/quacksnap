using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QuackSnap.Protocol;

/// <summary>
/// End-to-end encrypted relay envelope. The relay stores only ciphertext; the
/// envelope rides inside the push payload and lets the phone's notification
/// extension recover the file without the app running:
///   fileKey        random 256-bit, AES-256-GCM over the file bytes
///   kek            HKDF-SHA256(ECDH(ephemeral, recipient cert key), salt=epk)
///   keyIv/Ct/Tag   fileKey wrapped with the kek
///   sig            sender's ECDSA P-256 signature so pushes can't be forged
/// </summary>
public sealed record Envelope(
    int V,
    string BlobId,
    string RelayUrl,
    string Name,
    string Mime,
    long Size,
    string Epk,
    string KeyIv,
    string KeyCt,
    string KeyTag,
    string BlobIv,
    string BlobTag,
    string Sig)
{
    public string SignedData() =>
        $"qsenv1|{BlobId}|{RelayUrl}|{Name}|{Mime}|{Size}|{Epk}|{KeyIv}|{KeyCt}|{KeyTag}|{BlobIv}|{BlobTag}";
}

public static class EnvelopeCrypto
{
    private const string HkdfInfo = "quacksnap-kek-v1";

    public static (Envelope Envelope, byte[] BlobCiphertext) Seal(
        byte[] plaintext, string name, string mime,
        byte[] recipientCertDer, X509Certificate2 senderIdentity,
        string blobId, string relayUrl)
    {
        // Encrypt the file with a fresh key.
        byte[] fileKey = RandomNumberGenerator.GetBytes(32);
        byte[] blobIv = RandomNumberGenerator.GetBytes(12);
        byte[] blobCiphertext = new byte[plaintext.Length];
        byte[] blobTag = new byte[16];
        using (var aes = new AesGcm(fileKey, 16))
            aes.Encrypt(blobIv, plaintext, blobCiphertext, blobTag);

        // Wrap the file key to the recipient's certificate key (ECDH-ES + HKDF).
        using var recipientCert = new X509Certificate2(recipientCertDer);
        using var recipientKey = recipientCert.GetECDiffieHellmanPublicKey()
            ?? throw new CryptographicException("Recipient certificate has no EC key");
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] shared = ephemeral.DeriveRawSecretAgreement(recipientKey.PublicKey);
        byte[] epk = ExportX963(ephemeral);
        byte[] kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt: epk, info: Encoding.UTF8.GetBytes(HkdfInfo));

        byte[] keyIv = RandomNumberGenerator.GetBytes(12);
        byte[] keyCt = new byte[32];
        byte[] keyTag = new byte[16];
        using (var aes = new AesGcm(kek, 16))
            aes.Encrypt(keyIv, fileKey, keyCt, keyTag);

        var unsigned = new Envelope(1, blobId, relayUrl, name, mime, plaintext.Length,
            Base64Url.Encode(epk), Base64Url.Encode(keyIv), Base64Url.Encode(keyCt), Base64Url.Encode(keyTag),
            Base64Url.Encode(blobIv), Base64Url.Encode(blobTag), Sig: "");

        using var signer = senderIdentity.GetECDsaPrivateKey()
            ?? throw new CryptographicException("Sender identity has no EC signing key");
        byte[] sig = signer.SignData(Encoding.UTF8.GetBytes(unsigned.SignedData()), HashAlgorithmName.SHA256);

        CryptographicOperations.ZeroMemory(fileKey);
        CryptographicOperations.ZeroMemory(kek);
        return (unsigned with { Sig = Base64Url.Encode(sig) }, blobCiphertext);
    }

    /// <summary>Uncompressed X9.63 point (0x04 || X || Y), the format CryptoKit expects.</summary>
    private static byte[] ExportX963(ECDiffieHellman key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1 + (32 - p.Q.X!.Length));
        p.Q.Y!.CopyTo(point, 33 + (32 - p.Q.Y!.Length));
        return point;
    }
}
