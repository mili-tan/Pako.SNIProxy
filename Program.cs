using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pako.SNIProxy.Api;
using Pako.SNIProxy.Auth;
using Pako.SNIProxy.Configuration;
using Pako.SNIProxy.Core;
using Pako.SNIProxy.Dns;
using Pako.SNIProxy.Infrastructure;
using Pako.SNIProxy.Persistence;
using Pako.SNIProxy.Routing;
using Pako.SNIProxy.Throttling;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    o.SingleLine = true;
});

var options = new ProxyOptions();
builder.Configuration.GetSection(ProxyOptions.SectionName).Bind(options);
ConfigValidator.Validate(options);

builder.Services.AddSingleton(options);

// Kestrel hosts only the management API; the SNI proxy uses its own raw socket.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (options.ManagementApi.Enabled)
    {
        var apiAddress = IPAddress.TryParse(options.ManagementApi.ListenAddress, out var parsed)
            ? parsed
            : IPAddress.Loopback;
        kestrel.Listen(apiAddress, options.ManagementApi.ListenPort);
    }
    else
    {
        kestrel.Listen(IPAddress.Loopback, 0);
    }
});

// Infrastructure
builder.Services.AddSingleton<ConnectionRegistry>();

// Auth
builder.Services.AddSingleton<IClientAuthenticator>(_ => new IpCidrAuthenticator(options.ClientAuth));
builder.Services.AddSingleton(_ => new ClientPolicyResolver(options));

// Throttling
builder.Services.AddSingleton(_ => new ConnectionLimiter(options.ConnectionLimit));
builder.Services.AddSingleton(sp => new RateLimiter(
    sp.GetRequiredService<ClientPolicyResolver>(),
    sp.GetRequiredService<ILogger<RateLimiter>>()));

// Traffic quota + persistence (store registered before manager so it outlives it on dispose)
builder.Services.AddSingleton(_ =>
{
    var store = new TrafficStore(options.TrafficQuota.PersistPath);
    store.Initialize();
    return store;
});
builder.Services.AddSingleton(sp => new TrafficQuotaManager(
    sp.GetRequiredService<TrafficStore>(),
    options.TrafficQuota,
    sp.GetRequiredService<ClientPolicyResolver>(),
    sp.GetRequiredService<ILogger<TrafficQuotaManager>>()));

// Routing + DNS
builder.Services.AddSingleton(_ => new SiteRouter(options));
builder.Services.AddSingleton(_ => new DnsCache(options.DnsCacheMaxEntries));
builder.Services.AddSingleton(sp => new DnsResolverManager(
    sp.GetRequiredService<DnsCache>(),
    sp.GetRequiredService<ILogger<DnsResolverManager>>()));

// Relay (resolved from DI so it shares the same RateLimiter / TrafficQuotaManager singletons)
builder.Services.AddSingleton(sp => new ConnectionRelay(
    sp.GetRequiredService<RateLimiter>(),
    sp.GetRequiredService<TrafficQuotaManager>(),
    options.Connection.BufferSizeBytes,
    options.Connection.IdleTimeoutMs));

// Hosted SNI proxy service
builder.Services.AddHostedService<SniProxyService>();

var app = builder.Build();

if (options.ManagementApi.Enabled)
{
    app.UseMiddleware<ApiAuthMiddleware>();
    app.MapManagementApi();
    app.Lifetime.ApplicationStarted.Register(() =>
        app.Logger.LogInformation("Management API listening on {Addr}:{Port}",
            options.ManagementApi.ListenAddress, options.ManagementApi.ListenPort));
}

await app.RunAsync();
