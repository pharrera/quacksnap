using Makaretu.Dns;

namespace QuackSnap.Protocol;

/// <summary>
/// Advertises a DNS-SD service over mDNS for as long as the instance lives.
/// Cross-platform (raw mDNS sockets), used by the Windows pairing window and by
/// the dev harnesses so discovery is testable without a Windows machine.
/// </summary>
public sealed class BonjourAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _discovery;
    private readonly ServiceProfile _profile;

    public BonjourAdvertiser(string instanceName, string serviceType, int port, IDictionary<string, string>? txt = null)
    {
        _profile = new ServiceProfile(instanceName, serviceType, (ushort)port);
        if (txt != null)
        {
            foreach (var (key, value) in txt)
                _profile.AddProperty(key, value);
        }
        _discovery = new ServiceDiscovery();
        _discovery.Advertise(_profile);
        _discovery.Announce(_profile);
    }

    public void Dispose()
    {
        try { _discovery.Unadvertise(_profile); } catch { /* best effort */ }
        _discovery.Dispose();
    }
}
