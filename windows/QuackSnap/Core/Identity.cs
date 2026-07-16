using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuackSnap.Protocol;

namespace QuackSnap.Core;

/// <summary>
/// This machine's long-term identity: a stable device id and a self-signed TLS
/// certificate whose PFX is sealed with DPAPI so only this Windows user can read it.
/// </summary>
public sealed class Identity
{
    private const string PfxPassword = "quacksnap-local";

    public string DeviceId { get; }
    public string DeviceName { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    private Identity(string deviceId, X509Certificate2 cert)
    {
        DeviceId = deviceId;
        DeviceName = Environment.MachineName;
        Certificate = cert;
        Fingerprint = CertUtil.Fingerprint(cert);
    }

    public static Identity LoadOrCreate(string appDataDir)
    {
        var idPath = Path.Combine(appDataDir, "device-id.txt");
        var certPath = Path.Combine(appDataDir, "identity.bin");

        string deviceId;
        if (File.Exists(idPath))
        {
            deviceId = File.ReadAllText(idPath).Trim();
        }
        else
        {
            deviceId = Guid.NewGuid().ToString("N");
            File.WriteAllText(idPath, deviceId);
        }

        X509Certificate2 cert;
        if (File.Exists(certPath))
        {
            var pfx = ProtectedData.Unprotect(File.ReadAllBytes(certPath), null, DataProtectionScope.CurrentUser);
            cert = CertUtil.LoadPfx(pfx, PfxPassword);
        }
        else
        {
            cert = CertUtil.CreateIdentity(Environment.MachineName);
            var pfx = CertUtil.ExportPfx(cert, PfxPassword);
            File.WriteAllBytes(certPath, ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
            // Reload so the private key is usable by SChannel for TLS.
            cert = CertUtil.LoadPfx(pfx, PfxPassword);
        }

        Logger.Info($"Identity ready: {deviceId} fp={CertUtil.Fingerprint(cert)}");
        return new Identity(deviceId, cert);
    }
}
