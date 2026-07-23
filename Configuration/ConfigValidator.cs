using System.Net;

namespace Pako.SNIProxy.Configuration;

public static class ConfigValidator
{
    public static void Validate(ProxyOptions options)
    {
        var errors = new List<string>();

        if (options.ListenPort is < 1 or > 65535)
            errors.Add($"ListenPort {options.ListenPort} out of range (1-65535).");

        if (!IPAddress.TryParse(options.ListenAddress, out _) && options.ListenAddress != "::")
            errors.Add($"ListenAddress '{options.ListenAddress}' is not a valid IP address.");

        ValidateDns(options.Dns, "Dns", errors);

        foreach (var rule in options.SiteRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
                errors.Add("SiteRule has empty Pattern.");
            if (rule.Dns is not null)
                ValidateDns(rule.Dns, $"SiteRule[{rule.Pattern}].Dns", errors);
            if (!string.IsNullOrEmpty(rule.PinnedEndpoint) && !IPEndPoint.TryParse(rule.PinnedEndpoint, out _))
                errors.Add($"SiteRule[{rule.Pattern}].PinnedEndpoint '{rule.PinnedEndpoint}' is not a valid IP:Port.");
        }

        if (options.ClientAuth is { AllowAll: false, Whitelist.Count: 0 })
            errors.Add("ClientAuth.AllowAll is false but Whitelist is empty; all clients would be rejected.");

        for (int i = 0; i < options.ClientRules.Count; i++)
        {
            var rule = options.ClientRules[i];
            if (string.IsNullOrWhiteSpace(rule.Pattern))
                errors.Add($"ClientRules[{i}].Pattern is empty.");
            else if (!Infrastructure.IpUtils.TryParseNetwork(rule.Pattern, out _))
                errors.Add($"ClientRules[{i}].Pattern '{rule.Pattern}' is not a valid IP/CIDR.");

            if (rule.RateLimit is { Enabled: true, BytesPerSecond: <= 0 })
                errors.Add($"ClientRules[{i}].RateLimit.BytesPerSecond must be positive.");
            if (rule.TrafficQuota?.DailyLimitBytes is < 0)
                errors.Add($"ClientRules[{i}].TrafficQuota.DailyLimitBytes must be >= 0.");
            if (rule.TrafficQuota?.MonthlyLimitBytes is < 0)
                errors.Add($"ClientRules[{i}].TrafficQuota.MonthlyLimitBytes must be >= 0.");
        }

        if (options.RateLimit.Enabled && options.RateLimit.BytesPerSecond <= 0)
            errors.Add("RateLimit.BytesPerSecond must be positive.");

        if (options.ConnectionLimit is { Enabled: true, MaxPerClientIp: <= 0 })
            errors.Add("ConnectionLimit.MaxPerClientIp must be positive.");

        if (options.TrafficQuota.Enabled)
        {
            if (options.TrafficQuota.DailyLimitBytes <= 0)
                errors.Add("TrafficQuota.DailyLimitBytes must be positive.");
            if (string.IsNullOrWhiteSpace(options.TrafficQuota.PersistPath))
                errors.Add("TrafficQuota.PersistPath must not be empty.");
        }

        if (options.ManagementApi is { Enabled: true } && string.IsNullOrWhiteSpace(options.ManagementApi.AuthToken))
            errors.Add("ManagementApi.AuthToken must be set when ManagementApi is enabled.");

        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Invalid configuration:\n  - " + string.Join("\n  - ", errors));
    }

    private static void ValidateDns(DnsOptions dns, string path, List<string> errors)
    {
        if (dns.Mode == DnsMode.Udp && dns.Servers.Count == 0)
            errors.Add($"{path}.Mode is Udp but no Servers configured.");
        if (dns.Mode == DnsMode.DoH && dns.Servers.Count == 0)
            errors.Add($"{path}.Mode is DoH but no Servers configured.");
        if (dns.CacheTtlSeconds < 0)
            errors.Add($"{path}.CacheTtlSeconds must be >= 0.");
        if (dns.TimeoutMs <= 0)
            errors.Add($"{path}.TimeoutMs must be positive.");
    }
}
