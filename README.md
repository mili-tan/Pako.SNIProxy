# Pako.SNIProxy

English | [简体中文](./README.zh-CN.md)

A high-performance, production-ready **SNI transparent proxy** written in C# (.NET 10). It operates at the TLS handshake layer, parsing the SNI field from the ClientHello to route traffic. **It never decrypts or terminates TLS**, preserving end-to-end encryption and certificate validation.

---

## Highlights

- End-to-end encryption – TLS traffic is forwarded untouched; the origin's certificate is validated directly by the client.
- High performance – Async I/O, bounded caches, and a token-bucket rate limiter keep latency low even under load.
- Smart routing – Per‑site DNS policies (UDP/DoH), pinned IPs, and wildcard domain matching (`*.example.com`).
- Built‑in protection – IP/CIDR whitelist, per‑client rate limiting, connection limits, and daily/monthly traffic quotas with SQLite persistence.
- Runtime management – REST API to view stats, modify whitelists, adjust client policies, and force‑disconnect connections.
- Low‑memory friendly – Designed for a 1C1G VPS; memory usage is bounded and tunable.

---

## Quick Start

```bash
git clone ...
cd Pako.SNIProxy
dotnet build -c Release
dotnet run -c Release
```

By default, the proxy listens on port `443` (requires root or `CAP_NET_BIND_SERVICE`) and the management API on `127.0.0.1:9090`.

Configuration is in `appsettings.json` under the `SniProxy` section.

---

## How It Works

```
Client → [Auth check] → [Connection limit] → [Quota check]
       → [Parse SNI from ClientHello]
       → [Route by domain: pinned IP or DNS (cache → resolver)]
       → [Connect to origin and forward original ClientHello]
       → [Bidirectional relay with rate limiting & traffic metering]
```

Because the proxy only forwards raw TLS bytes, the client receives the **real certificate** from the origin – no certificate spoofing or validation bypass is needed.

---

## Detailed Configuration Reference

All settings live under the `SniProxy` section in `appsettings.json`. The following tables describe each field with default values and descriptions. For per‑site and per‑client rules, see the examples below.

### Basics

| Field | Default | Description |
|-------|---------|-------------|
| `ListenPort` | `443` | SNI proxy listen port |
| `ListenAddress` | `"::"` | Listen address (`::` = dual-stack IPv4/IPv6) |
| `RouteMode` | `"Whitelist"` | `Whitelist` or `AllowAll` |
| `DnsCacheMaxEntries` | `10000` | Max DNS cache entries (evicted by expiry when exceeded) |

### Global DNS (`Dns`)

| Field | Description |
|-------|-------------|
| `Mode` | `System` / `Udp` / `DoH` |
| `Servers` | UDP: `["223.5.5.5:53"]`; DoH: `["https://dns.alidns.com/dns-query"]` |
| `CacheTtlSeconds` | Cache TTL cap (effective TTL is the smaller of the DNS-returned TTL and this value) |
| `TimeoutMs` | Resolution timeout |

### Site Rules (`SiteRules`)

Matched in order; the first match wins.

```jsonc
"SiteRules": [
  { "Pattern": "*.internal.corp", "Dns": { "Mode": "Udp", "Servers": ["10.0.0.1:53"] } },
  { "Pattern": "*.baidu.com",     "Dns": { "Mode": "Udp", "Servers": ["223.5.5.5:53"] } },
  { "Pattern": "*.google.com",    "Dns": { "Mode": "DoH", "Servers": ["https://dns.alidns.com/dns-query"] } },
  { "Pattern": "example.com",     "PinnedEndpoint": "93.184.216.34:443" },
  { "Pattern": "*.github.com" }   // no Dns specified → uses the global DNS
]
```

- `Pattern`: exact domain or wildcard `*.example.com` (matches subdomains, not `example.com` itself)
- `Dns`: site‑specific DNS (omitted → uses global `Dns`)
- `PinnedEndpoint`: pinned `IP:Port`, skips DNS resolution

### Client Authentication (`ClientAuth`)

```jsonc
"ClientAuth": {
  "AllowAll": true,                          // true = allow any IP; false = whitelist only
  "Whitelist": ["192.168.1.0/24", "10.0.0.1", "fd00::/64"]
}
```

### Global Rate Limit (`RateLimit`) / Connection Limit (`ConnectionLimit`) / Traffic Quota (`TrafficQuota`)

```jsonc
"RateLimit":       { "Enabled": true, "BytesPerSecond": 10485760, "BurstBytes": 20971520 },
"ConnectionLimit": { "Enabled": true, "MaxPerClientIp": 50, "MaxTotal": 1000 },
"TrafficQuota":    { "Enabled": true, "DailyLimitBytes": 10737418240, "MonthlyLimitBytes": 214748364800, "PersistPath": "./data/traffic.db" }
```

### Per‑Client Policy (`ClientRules`)

Override rate limit and quota for a single IP / subnet. **Matched in order; the first match wins.** Unmatched clients use the global defaults above. Fields left unset within a rule also fall back to the global defaults.

```jsonc
"ClientRules": [
  {
    "Pattern": "192.168.1.100",                       // single IP
    "RateLimit": { "Enabled": true, "BytesPerSecond": 1048576, "BurstBytes": 2097152 },
    "TrafficQuota": { "DailyLimitBytes": 1073741824, "MonthlyLimitBytes": 10737418240 }
  },
  {
    "Pattern": "10.0.0.0/8",                          // subnet
    "RateLimit": { "Enabled": false },                // no rate limit for this subnet
    "TrafficQuota": { "Enabled": false }              // no quota accounting for this subnet
  }
]
```

- `RateLimit`: omitted → uses global; `Enabled:false` → no rate limit for this client
- `TrafficQuota`: `Enabled` / `DailyLimitBytes` / `MonthlyLimitBytes` are all optional; omitted fields fall back to global

Use `GET /api/client-rules/resolve?ip=<ip>` to see the effective policy for any IP (including fallback results).

### Management API (`ManagementApi`)

```jsonc
"ManagementApi": { "Enabled": true, "ListenPort": 9090, "ListenAddress": "127.0.0.1", "AuthToken": "change-me-to-a-strong-secret" }
```

In production, always change `AuthToken` and keep `ListenAddress` loopback‑only (or restrict access via firewall).

### Connection Settings (`Connection`)

| Field | Default | Description |
|-------|---------|-------------|
| `InitialReadTimeoutMs` | `5000` | Timeout for reading the ClientHello |
| `ConnectTimeoutMs` | `5000` | Timeout for connecting to the origin |
| `IdleTimeoutMs` | `300000` | Idle timeout when no data is flowing |
| `BufferSizeBytes` | `16384` | Relay buffer per direction (larger = more throughput, more memory) |
| `Backlog` | `1024` | Listen backlog |

---

## Management API

All endpoints require `Authorization: Bearer <AuthToken>`.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/status` | GET | Runtime status, connection counts, memory, uptime |
| `/api/whitelist/sites` | GET/POST | Query / add‑or‑update site whitelist |
| `/api/whitelist/sites/{pattern}` | DELETE | Remove a site rule |
| `/api/whitelist/clients` | GET/POST | Query / add client whitelist |
| `/api/whitelist/clients/{entry}` | DELETE | Remove a client whitelist entry |
| `/api/whitelist/clients/allow-all` | PUT | Set `{"allowAll":true/false}` |
| `/api/client-rules` | GET/POST | Query / add‑or‑update client policies |
| `/api/client-rules/{pattern}` | DELETE | Remove a client policy |
| `/api/client-rules/resolve?ip=` | GET | Effective policy for an IP (with fallback) |
| `/api/traffic` | GET | Per‑client traffic statistics |
| `/api/traffic?ip=` | GET | Daily/monthly traffic for a specific IP |
| `/api/traffic/reset?ip=` | POST | Reset traffic counters for an IP |
| `/api/connections` | GET | Current active connections |
| `/api/connections/{id}` | DELETE | Force‑disconnect a connection |
| `/api/config/route-mode` | GET/PUT | Query / switch route mode |
| `/api/dns/cache` | GET/DELETE | Query / clear the DNS cache |

Example:
```bash
TOKEN="your-secret"
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:9090/api/status
curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"pattern":"*.example.com"}' http://127.0.0.1:9090/api/whitelist/sites
```

---

## Transparent Proxy Deployment

To steer traffic into the proxy, you need network‑layer redirection:

- **DNS hijacking** – resolve whitelisted domains to the proxy's IP.
- **iptables / nftables** – redirect outbound port 443 to the proxy's listening port.
  ```bash
  iptables -t nat -A OUTPUT -p tcp --dport 443 -j REDIRECT --to-port 8443
  ```

If the proxy listens on port 443 directly, no redirection is needed (but requires root privileges).

---

## Performance & Memory Tuning

Defaults are conservative and suit a 1C1G VPS. The main memory consumers are:

- Relay buffers – about `MaxTotal × 2 × BufferSizeBytes`
- DNS cache – bounded by `DnsCacheMaxEntries`
- Traffic counters and SQLite – auto‑eviction and periodic cleanup

**Recommended presets:**

| Spec | `MaxPerClientIp` | `MaxTotal` | `BufferSizeBytes` | `Backlog` | `DnsCacheMaxEntries` | Buffer memory | systemd `MemoryMax` |
|------|------------------|------------|-------------------|-----------|----------------------|---------------|----------------------|
| 1C1G | 30               | 500        | 16384             | 512       | 5000                 | ~16 MB        | 512M                 |
| 2C2G | 50               | 1500       | 32768             | 1024      | 10000                | ~94 MB        | 1G                   |
| 2C4G+ | 100             | 5000       | 65536             | 2048      | 50000                | ~625 MB       | 2G                   |

- Manage the process with systemd and set `MemoryMax` as a safety net (see presets above).
- Keep `IdleTimeoutMs` reasonable to reclaim idle connections promptly.
- Keep the log level at `Information`; avoid enabling `Debug` on the hot path.
- The relay is async/I‑O bound, so extra cores help mainly under very high connection counts.

<details>
<summary><b>1C1G</b> — 1 CPU / 1 GB</summary>

```jsonc
"ConnectionLimit":    { "Enabled": true, "MaxPerClientIp": 30,  "MaxTotal": 500 },
"Connection":         { "BufferSizeBytes": 16384, "Backlog": 512 },
"DnsCacheMaxEntries": 5000
```

</details>

<details>
<summary><b>2C2G</b> — 2 CPU / 2 GB</summary>

```jsonc
"ConnectionLimit":    { "Enabled": true, "MaxPerClientIp": 50,  "MaxTotal": 1500 },
"Connection":         { "BufferSizeBytes": 32768, "Backlog": 1024 },
"DnsCacheMaxEntries": 10000
```

</details>

<details>
<summary><b>2C4G+</b> — 2+ CPU / 4 GB+</summary>

```jsonc
"ConnectionLimit":    { "Enabled": true, "MaxPerClientIp": 100, "MaxTotal": 5000 },
"Connection":         { "BufferSizeBytes": 65536, "Backlog": 2048 },
"DnsCacheMaxEntries": 50000
```

</details>

---

## Security Recommendations

- Change `ManagementApi.AuthToken` to a strong random value; bind the management port to loopback only.
- For public deployments, set `ClientAuth.AllowAll=false` and configure a whitelist.
- Enable rate limiting and traffic quotas as needed to prevent abuse.
- Pinned IPs (`PinnedEndpoint`) can bypass DNS poisoning, but you must maintain IP validity yourself.

---

## Project Structure

```
Pako.SNIProxy/
├── Program.cs                  # Entry, DI, Kestrel host
├── Configuration/              # Strongly‑typed options + validation
├── Core/                       # SNI parsing, relay, listener, connection context
├── Dns/                        # ARSoft resolvers, cache, per‑rule isolation
├── Routing/                    # Site matching, wildcard
├── Auth/                       # IP/CIDR auth, policy resolution
├── Throttling/                 # Rate limiter, connection limit, quota
├── Persistence/                # SQLite traffic storage
├── Api/                        # REST API + auth middleware
└── Infrastructure/             # IP utilities, connection registry
```

---

## Dependencies

| Package | Purpose |
|---------|---------|
| [ARSoft.Tools.Net](https://github.com/alexreinert/ARSoft.Tools.Net) | DNS client (UDP/TCP/DoH) |
| [IPNetwork2](https://github.com/lduchosal/ipnetwork) | IP/CIDR matching |
| Microsoft.Data.Sqlite | Traffic persistence |

---

## License

MIT
