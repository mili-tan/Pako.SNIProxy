using System.Net;

namespace Pako.SNIProxy.Dns;

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<DnsAnswer>> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.Select(a => new DnsAnswer(a, 300)).ToList();
        }
        catch
        {
            return Array.Empty<DnsAnswer>();
        }
    }
}
