namespace Pako.SNIProxy.Configuration;

public sealed class ProxyOptions
{
    public const string SectionName = "SniProxy";

    public int ListenPort { get; set; } = 443;
    public string ListenAddress { get; set; } = "::";

    public RouteMode RouteMode { get; set; } = RouteMode.Whitelist;

    public DnsOptions Dns { get; set; } = new();
    public List<SiteRule> SiteRules { get; set; } = new();

    public ClientAuthOptions ClientAuth { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();
    public ConnectionLimitOptions ConnectionLimit { get; set; } = new();
    public TrafficQuotaOptions TrafficQuota { get; set; } = new();
    public List<ClientRule> ClientRules { get; set; } = new();
    public int DnsCacheMaxEntries { get; set; } = 10000;
    public ManagementApiOptions ManagementApi { get; set; } = new();
    public ConnectionOptions Connection { get; set; } = new();
}

public enum RouteMode
{
    Whitelist,
    AllowAll
}

public sealed class DnsOptions
{
    public DnsMode Mode { get; set; } = DnsMode.System;
    public List<string> Servers { get; set; } = new();
    public int CacheTtlSeconds { get; set; } = 300;
    public int TimeoutMs { get; set; } = 3000;
}
public enum DnsMode
{
    System,
    Udp,
    DoH
}

public sealed class SiteRule
{
    public string Pattern { get; set; } = string.Empty;
    public DnsOptions? Dns { get; set; }
    public string? PinnedEndpoint { get; set; }
}

public sealed class ClientAuthOptions
{
    public bool AllowAll { get; set; } = true;
    public List<string> Whitelist { get; set; } = new();
}

public sealed class RateLimitOptions
{
    public bool Enabled { get; set; }
    public long BytesPerSecond { get; set; } = 10 * 1024 * 1024;
    public long BurstBytes { get; set; } = 20 * 1024 * 1024;
}

public sealed class ConnectionLimitOptions
{
    public bool Enabled { get; set; }
    public int MaxPerClientIp { get; set; } = 50;
    public int MaxTotal { get; set; } = 1000;
}

public sealed class TrafficQuotaOptions
{
    public bool Enabled { get; set; }
    public long DailyLimitBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public long MonthlyLimitBytes { get; set; } = 200L * 1024 * 1024 * 1024;
    public string PersistPath { get; set; } = "./data/traffic.db";
}

public sealed class ClientRule
{
    public string Pattern { get; set; } = string.Empty;
    public RateLimitOptions? RateLimit { get; set; }
    public QuotaLimitOptions? TrafficQuota { get; set; }
}

public sealed class QuotaLimitOptions
{
    public bool? Enabled { get; set; }
    public long? DailyLimitBytes { get; set; }
    public long? MonthlyLimitBytes { get; set; }
}

public sealed class ManagementApiOptions
{
    public bool Enabled { get; set; } = true;
    public int ListenPort { get; set; } = 9090;
    public string ListenAddress { get; set; } = "127.0.0.1";
    public string AuthToken { get; set; } = string.Empty;
}

public sealed class ConnectionOptions
{
    public int InitialReadTimeoutMs { get; set; } = 5000;
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int IdleTimeoutMs { get; set; } = 300000;
    public int BufferSizeBytes { get; set; } = 16384;
    public int Backlog { get; set; } = 1024;
}
