using System.Net;
using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;

namespace Pako.SNIProxy.Dns;

public sealed class ArSoftDnsResolver : IDnsResolver, IDisposable
{
    private readonly DnsClient _client;
    private readonly DnsQueryOptions _queryOptions = new() { IsRecursionDesired = true };

    private ArSoftDnsResolver(DnsClient client)
    {
        _client = client;
    }

    public static ArSoftDnsResolver CreateUdp(IEnumerable<string> servers, int timeoutMs)
    {
        var ips = servers.Select(ParseUdpServer).Distinct().ToArray();
        if (ips.Length == 0)
            throw new ArgumentException("At least one UDP DNS server is required.");

        var client = new DnsClient(ips, timeoutMs);
        return new ArSoftDnsResolver(client);
    }

    public static ArSoftDnsResolver CreateDoH(IEnumerable<string> servers, int timeoutMs)
    {
        var transports = servers
            .Select(s => (IClientTransport)new HttpsClientTransport(new Uri(s)))
            .ToArray();
        if (transports.Length == 0)
            throw new ArgumentException("At least one DoH server URI is required.");

        var client = new DnsClient(new[] { IPAddress.Loopback }, transports, disposeTransport: true, timeoutMs);
        return new ArSoftDnsResolver(client);
    }

    private static IPAddress ParseUdpServer(string server)
    {
        if (IPEndPoint.TryParse(server, out var ep))
            return ep.Address;

        var parts = server.Split(':');
        if (parts.Length >= 1 && IPAddress.TryParse(parts[0], out var ip))
            return ip;

        throw new ArgumentException($"Invalid UDP DNS server address: {server}");
    }

    public async Task<IReadOnlyList<DnsAnswer>> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (!DomainName.TryParse(host, out var name) || name is null)
            return Array.Empty<DnsAnswer>();

        var aTask = QueryAsync(name, RecordType.A, cancellationToken);
        var aaaaTask = QueryAsync(name, RecordType.Aaaa, cancellationToken);

        var aRecords = await aTask.ConfigureAwait(false);
        var aaaaRecords = await aaaaTask.ConfigureAwait(false);

        if (aRecords.Count == 0 && aaaaRecords.Count == 0)
            return Array.Empty<DnsAnswer>();

        var results = new List<DnsAnswer>(aRecords.Count + aaaaRecords.Count);
        results.AddRange(aRecords);
        results.AddRange(aaaaRecords);
        return results;
    }

    private async Task<List<DnsAnswer>> QueryAsync(DomainName name, RecordType type, CancellationToken cancellationToken)
    {
        try
        {
            var message = await _client
                .ResolveAsync(name, type, RecordClass.INet, _queryOptions, cancellationToken)
                .ConfigureAwait(false);

            if (message is null)
                return new List<DnsAnswer>();

            var list = new List<DnsAnswer>();
            foreach (var record in message.AnswerRecords)
            {
                switch (record)
                {
                    case ARecord a:
                        list.Add(new DnsAnswer(a.Address, a.TimeToLive));
                        break;
                    case AaaaRecord aaaa:
                        list.Add(new DnsAnswer(aaaa.Address, aaaa.TimeToLive));
                        break;
                }
            }
            return list;
        }
        catch
        {
            return new List<DnsAnswer>();
        }
    }

    public void Dispose()
    {
        ((IDisposable)_client).Dispose();
    }
}
