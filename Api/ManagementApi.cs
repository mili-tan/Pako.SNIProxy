using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pako.SNIProxy.Auth;
using Pako.SNIProxy.Configuration;
using Pako.SNIProxy.Dns;
using Pako.SNIProxy.Infrastructure;
using Pako.SNIProxy.Routing;
using Pako.SNIProxy.Throttling;

namespace Pako.SNIProxy.Api;

public static class ManagementApi
{
    private static readonly DateTime StartTimeUtc = DateTime.UtcNow;

    public static IEndpointRouteBuilder MapManagementApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (ConnectionRegistry registry, ConnectionLimiter limiter, ProxyOptions options) =>
        {
            var uptime = DateTime.UtcNow - StartTimeUtc;
            return Results.Ok(new
            {
                status = "running",
                uptimeSeconds = (long)uptime.TotalSeconds,
                uptime = uptime.ToString(@"dd\.hh\:mm\:ss"),
                routeMode = options.RouteMode.ToString(),
                activeConnections = registry.Count,
                totalConnections = limiter.TotalConnections,
                workingSetMb = Environment.WorkingSet / (1024.0 * 1024.0),
                gcTotalMemoryMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0),
                threadCount = System.Diagnostics.Process.GetCurrentProcess().Threads.Count
            });
        });

        MapSiteWhitelist(api);
        MapClientWhitelist(api);
        MapClientRules(api);
        MapTraffic(api);
        MapConnections(api);
        MapConfig(api);
        MapDns(api);

        return app;
    }

    private static void MapSiteWhitelist(IEndpointRouteBuilder api)
    {
        api.MapGet("/whitelist/sites", (SiteRouter router) =>
            Results.Ok(new { patterns = router.GetPatterns() }));

        api.MapPost("/whitelist/sites", (SiteRuleRequest req, SiteRouter router) =>
        {
            if (string.IsNullOrWhiteSpace(req.Pattern))
                return Results.BadRequest(new { error = "Pattern is required." });

            try
            {
                router.AddOrUpdateRule(new SiteRule
                {
                    Pattern = req.Pattern,
                    PinnedEndpoint = req.PinnedEndpoint,
                    Dns = req.Dns
                });
                return Results.Ok(new { message = "Rule added/updated.", pattern = req.Pattern });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapDelete("/whitelist/sites/{pattern}", (string pattern, SiteRouter router) =>
        {
            return router.RemoveRule(pattern)
                ? Results.Ok(new { message = "Rule removed.", pattern })
                : Results.NotFound(new { error = "Pattern not found." });
        });
    }

    private static void MapClientWhitelist(IEndpointRouteBuilder api)
    {
        api.MapGet("/whitelist/clients", (IClientAuthenticator auth) =>
            Results.Ok(new { allowAll = auth.AllowAll, whitelist = auth.GetWhitelist() }));

        api.MapPost("/whitelist/clients", (ClientEntryRequest req, IClientAuthenticator auth) =>
        {
            if (string.IsNullOrWhiteSpace(req.Entry))
                return Results.BadRequest(new { error = "Entry is required." });
            try
            {
                auth.AddEntry(req.Entry);
                return Results.Ok(new { message = "Entry added.", entry = req.Entry });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapDelete("/whitelist/clients/{entry}", (string entry, IClientAuthenticator auth) =>
        {
            return auth.RemoveEntry(entry)
                ? Results.Ok(new { message = "Entry removed.", entry })
                : Results.NotFound(new { error = "Entry not found." });
        });

        api.MapPut("/whitelist/clients/allow-all", (AllowAllRequest req, IClientAuthenticator auth) =>
        {
            auth.SetAllowAll(req.AllowAll);
            return Results.Ok(new { allowAll = auth.AllowAll });
        });
    }

    private static void MapClientRules(IEndpointRouteBuilder api)
    {
        api.MapGet("/client-rules", (ClientPolicyResolver resolver) =>
            Results.Ok(new { rules = resolver.GetRules() }));

        api.MapGet("/client-rules/resolve", (string? ip, ClientPolicyResolver resolver) =>
        {
            if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var addr))
                return Results.BadRequest(new { error = "A valid 'ip' query parameter is required." });

            var policy = resolver.Resolve(addr);
            return Results.Ok(new
            {
                ip,
                rateLimitEnabled = policy.RateLimitEnabled,
                rateBytesPerSecond = policy.RateBytesPerSecond,
                rateBurstBytes = policy.RateBurstBytes,
                quotaEnabled = policy.QuotaEnabled,
                dailyLimitBytes = policy.DailyLimitBytes,
                monthlyLimitBytes = policy.MonthlyLimitBytes
            });
        });

        api.MapPost("/client-rules", (ClientRule req, ClientPolicyResolver resolver) =>
        {
            if (string.IsNullOrWhiteSpace(req.Pattern))
                return Results.BadRequest(new { error = "Pattern is required." });
            try
            {
                resolver.AddOrUpdate(req);
                return Results.Ok(new { message = "Client rule added/updated.", pattern = req.Pattern });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapDelete("/client-rules/{pattern}", (string pattern, ClientPolicyResolver resolver) =>
        {
            return resolver.Remove(pattern)
                ? Results.Ok(new { message = "Client rule removed.", pattern })
                : Results.NotFound(new { error = "Pattern not found." });
        });
    }

    private static void MapTraffic(IEndpointRouteBuilder api)
    {
        api.MapGet("/traffic", (string? ip, TrafficQuotaManager quota, ConnectionRegistry registry) =>
        {
            if (!string.IsNullOrEmpty(ip))
            {
                if (!IPAddress.TryParse(ip, out var addr))
                    return Results.BadRequest(new { error = "Invalid IP." });

                var (day, month) = quota.GetUsage(addr);
                return Results.Ok(new { ip, dayBytes = day, monthBytes = month });
            }

            var connections = registry.Snapshot()
                .GroupBy(c => c.ClientIp)
                .Select(g =>
                {
                    var (day, month) = quota.GetUsage(g.Key);
                    return new
                    {
                        ip = g.Key.ToString(),
                        activeConnections = g.Count(),
                        sessionBytesIn = g.Sum(c => c.GetBytesIn()),
                        sessionBytesOut = g.Sum(c => c.GetBytesOut()),
                        dayBytes = day,
                        monthBytes = month
                    };
                })
                .OrderByDescending(x => x.dayBytes)
                .ToList();

            return Results.Ok(new { clients = connections });
        });

        api.MapPost("/traffic/reset", (string? ip, TrafficQuotaManager quota) =>
        {
            if (string.IsNullOrEmpty(ip))
                return Results.BadRequest(new { error = "ip query parameter is required." });
            if (!IPAddress.TryParse(ip, out var addr))
                return Results.BadRequest(new { error = "Invalid IP." });

            quota.Reset(addr);
            return Results.Ok(new { message = "Traffic counters reset.", ip });
        });
    }

    private static void MapConnections(IEndpointRouteBuilder api)
    {
        api.MapGet("/connections", (ConnectionRegistry registry) =>
        {
            var connections = registry.Snapshot().Select(c => new
            {
                id = c.Id,
                clientIp = c.ClientIp.ToString(),
                sni = c.Sni,
                target = c.TargetEndpoint?.ToString(),
                connectedAtUtc = c.ConnectedAtUtc,
                durationSeconds = (long)(DateTime.UtcNow - c.ConnectedAtUtc).TotalSeconds,
                bytesIn = c.GetBytesIn(),
                bytesOut = c.GetBytesOut()
            }).OrderByDescending(c => c.connectedAtUtc).ToList();

            return Results.Ok(new { count = connections.Count, connections });
        });

        api.MapDelete("/connections/{id:long}", (long id, ConnectionRegistry registry) =>
        {
            return registry.TryDisconnect(id)
                ? Results.Ok(new { message = "Connection disconnected.", id })
                : Results.NotFound(new { error = "Connection not found." });
        });
    }

    private static void MapConfig(IEndpointRouteBuilder api)
    {
        api.MapGet("/config/route-mode", (SiteRouter router) =>
            Results.Ok(new { routeMode = router.Mode.ToString() }));

        api.MapPut("/config/route-mode", (RouteModeRequest req, SiteRouter router) =>
        {
            if (!Enum.TryParse<RouteMode>(req.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = "Mode must be 'Whitelist' or 'AllowAll'." });

            router.SetMode(mode);
            return Results.Ok(new { routeMode = router.Mode.ToString() });
        });
    }

    private static void MapDns(IEndpointRouteBuilder api)
    {
        api.MapGet("/dns/cache", (DnsResolverManager dns) =>
            Results.Ok(new { entries = dns.Cache.Count }));

        api.MapDelete("/dns/cache", (DnsResolverManager dns) =>
        {
            dns.Cache.Clear();
            return Results.Ok(new { message = "DNS cache cleared." });
        });
    }
}

public sealed record SiteRuleRequest(string Pattern, string? PinnedEndpoint, DnsOptions? Dns);
public sealed record ClientEntryRequest(string Entry);
public sealed record AllowAllRequest(bool AllowAll);
public sealed record RouteModeRequest(string Mode);
