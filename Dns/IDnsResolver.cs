using System.Net;

namespace Pako.SNIProxy.Dns;

public interface IDnsResolver
{
    Task<IReadOnlyList<DnsAnswer>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

public readonly record struct DnsAnswer(IPAddress Address, int TtlSeconds);
