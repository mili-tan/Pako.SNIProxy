# Pako.SNIProxy

English | [简体中文](./README.zh-CN.md)

A high-performance, production-ready **SNI transparent proxy** written in C# (.NET 10). It operates at the TLS handshake layer, parsing the SNI field from the ClientHello to route traffic. It **never decrypts or terminates TLS**, preserving end-to-end encryption and certificate validation.

## Features

- **SNI transparent proxy**: parses the SNI from the TLS ClientHello, connects directly to the origin, and relays encrypted traffic bidirectionally
- **Automatic domain resolution**: supports system DNS / custom UDP DNS / DoH (DNS over HTTPS), with TTL caching
- **Per-site DNS**: different sites can use different DNS servers (e.g. internal DNS for intranet, DoH for external)
- **Pinned IP**: a site can skip DNS resolution and connect directly to a fixed `IP:Port`
- **Site whitelist / allow-all**: whitelist mode by default, with wildcard support (`*.example.com`)
- **Client authentication**: IP / CIDR whitelist (powered by [IPNetwork2](https://github.com/lduchosal/ipnetwork)); allows any IP by default
- **Per-client policy**: override rate limit and traffic quota for a single IP / subnet; unmatched clients fall back to the global defaults
- **Rate limiting**: token-bucket algorithm, per client IP
- **Concurrent connection limits**: per client IP and global total
- **Traffic quota**: daily / monthly per-client traffic caps, persisted in SQLite
- **Management REST API**: view status, add/remove whitelist & client policies, query traffic, and force-disconnect at runtime
- **Low-memory friendly**: bounded DNS cache, idle-counter eviction, SQLite WAL checkpointing — suitable for a 1C1G VPS

## How It Works

```
Client TCP connection
  ├─ [1] Client auth (IP/CIDR)              fail → close
  ├─ [2] Concurrent connection check        over limit → close
  ├─ [3] Traffic quota check (day/month)    over limit → close
  ├─ [4] Read TLS ClientHello, parse SNI    fail → close
  ├─ [5] Site routing decision
  │       ├─ PinnedEndpoint matched → connect to fixed IP
  │       ├─ Whitelist mode, no match → reject
  │       └─ Rule matched / AllowAll → use the corresponding DNS config
  ├─ [6] DNS resolve (cache → configured DNS)  fail → close
  ├─ [7] Connect to origin (port 443)       fail → close
  ├─ [8] Forward the already-read ClientHello
  └─ [9] Bidirectional relay (via rate limiter + traffic meter)
                                            either side closes/times out → close both
```

Because the proxy only forwards encrypted bytes, the client validates the **origin's real certificate** — certificate verification works normally (no need to skip validation).

## Requirements

- .NET 10 SDK
- Linux (recommended) / Windows
- Listening on port 443 requires root / CAP_NET_BIND_SERVICE

## Build & Run

```bash
cd Pako.SNIProxy
dotnet build -c Release
dotnet run -c Release
# or publish a standalone executable
dotnet publish -c Release -r linux-x64 --self-contained false -o ./publish
```

By default it listens on `443` (SNI proxy) and `127.0.0.1:9090` (management API).

## Configuration (appsettings.json)

All settings live under the `SniProxy` section.

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
- `Dns`: site-specific DNS (omitted → uses global `Dns`)
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

### Per-Client Policy (`ClientRules`)

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

> Use `GET /api/client-rules/resolve?ip=<ip>` to see the effective policy for any IP (including fallback results).

### Management API (`ManagementApi`)

```jsonc
"ManagementApi": { "Enabled": true, "ListenPort": 9090, "ListenAddress": "127.0.0.1", "AuthToken": "change-me-to-a-strong-secret" }
```

> In production, always change `AuthToken` and keep `ListenAddress` loopback-only (or restrict access via firewall).

### Connection Settings (`Connection`)

| Field | Default | Description |
|-------|---------|-------------|
| `InitialReadTimeoutMs` | `5000` | Timeout for reading the ClientHello |
| `ConnectTimeoutMs` | `5000` | Timeout for connecting to the origin |
| `IdleTimeoutMs` | `300000` | Idle timeout when no data is flowing |
| `BufferSizeBytes` | `16384` | Relay buffer per direction (larger = more throughput, more memory) |
| `Backlog` | `1024` | Listen backlog |

## Management API

All endpoints require `Authorization: Bearer <AuthToken>`.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/status` | GET | Runtime status, connection counts, memory, uptime |
| `/api/whitelist/sites` | GET/POST | Query / add-or-update site whitelist |
| `/api/whitelist/sites/{pattern}` | DELETE | Remove a site rule |
| `/api/whitelist/clients` | GET/POST | Query / add client whitelist |
| `/api/whitelist/clients/{entry}` | DELETE | Remove a client whitelist entry |
| `/api/whitelist/clients/allow-all` | PUT | Set `{"allowAll":true/false}` |
| `/api/client-rules` | GET/POST | Query / add-or-update client policies |
| `/api/client-rules/{pattern}` | DELETE | Remove a client policy |
| `/api/client-rules/resolve?ip=` | GET | Effective policy for an IP (with fallback) |
| `/api/traffic` | GET | Per-client traffic statistics |
| `/api/traffic?ip=` | GET | Daily/monthly traffic for a specific IP |
| `/api/traffic/reset?ip=` | POST | Reset traffic counters for an IP |
| `/api/connections` | GET | Current active connections |
| `/api/connections/{id}` | DELETE | Force-disconnect a connection |
| `/api/config/route-mode` | GET/PUT | Query / switch route mode |
| `/api/dns/cache` | GET/DELETE | Query / clear the DNS cache |

Example:

```bash
TOKEN="your-secret"
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:9090/api/status
curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"pattern":"*.example.com"}' http://127.0.0.1:9090/api/whitelist/sites
```

## Transparent Proxy Deployment

The SNI proxy itself only routes by SNI; you need a network-layer mechanism to steer target traffic into the proxy port.

### Option 1: DNS hijacking (gateway / router)

Point clients' DNS at a local DNS service (e.g. dnsmasq) that resolves whitelisted domains to the proxy IP. Client traffic then naturally reaches the proxy's port 443.

### Option 2: iptables redirect (local transparent proxy)

```bash
# Redirect outbound 443 traffic to local 8443 (when the proxy listens on 8443)
iptables -t nat -A OUTPUT -p tcp --dport 443 -j REDIRECT --to-port 8443
```

### Option 3: nftables

```bash
nft add rule ip nat output tcp dport 443 redirect to :8443
```

> Note: the proxy's listen port must match the redirect target. If the proxy listens on 443 directly, no redirect is needed (requires root).

## Performance & Memory Tuning

Defaults are conservative and tuned for a low-spec VPS. Runtime memory mainly comes from four sources: per-connection relay buffers, the DNS cache, traffic counters, and SQLite.

| Concern | Default | Notes |
|---------|---------|-------|
| Per-connection buffer | `BufferSizeBytes=16384` | ~`2 × BufferSizeBytes` per connection (one buffer per direction). Raise to 32768/65536 for throughput, at the cost of memory |
| Max connections | `MaxTotal=1000` | Each connection also uses 2 sockets + an idle watchdog timer |
| Per-IP connections | `MaxPerClientIp=50` | Prevents a single client from exhausting resources |
| DNS cache | `DnsCacheMaxEntries=10000` | Bounded; evicted by expiry when exceeded — no unbounded growth |
| Traffic counters | Auto-eviction | Client counters idle for >1h are removed from memory after flushing |
| SQLite | WAL + periodic checkpoint | `wal_checkpoint(TRUNCATE)` every 10 min + cleanup of records older than 60 days |

**Memory rule of thumb:**

```
buffer memory ≈ MaxTotal × 2 × BufferSizeBytes
```

On top of that, budget ~80–120 MB for the .NET runtime, the DNS cache, and SQLite. Use the sum to size `MemoryMax`.

### Recommended presets

| Spec | MaxPerClientIp | MaxTotal | BufferSizeBytes | Backlog | DnsCacheMaxEntries | systemd `MemoryMax` |
|------|---------------|----------|-----------------|---------|--------------------|---------------------|
| **1C1G** | 30 | 500 | 16384 | 512 | 5000 | 512M |
| **2C2G** | 50 | 1500 | 32768 | 1024 | 10000 | 1G |
| **2C4G+** | 100 | 5000 | 65536 | 2048 | 50000 | 2G |

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

Additional tips:

- Manage the process with systemd and set `MemoryMax` as a safety net (see presets above)
- Keep `IdleTimeoutMs` reasonable to reclaim idle connections promptly
- Keep the log level at `Information`; avoid enabling `Debug` on the hot path
- The relay is async/I-O bound, so extra cores help mainly under very high connection counts

## Security Recommendations

- Change `ManagementApi.AuthToken` to a strong random value; bind the management port to loopback only
- For public deployments, set `ClientAuth.AllowAll=false` and configure a whitelist
- Enable rate limiting and traffic quotas as needed to prevent abuse
- Pinned IPs (`PinnedEndpoint`) can bypass DNS poisoning, but you must maintain IP validity yourself

## Project Structure

```
Pako.SNIProxy/
├── Program.cs                  # Entry point, DI orchestration, Kestrel (management API)
├── appsettings.json            # Configuration
├── Configuration/              # Strongly-typed options + validation
├── Core/                       # SNI parsing, connection relay, listener service, connection context
├── Dns/                        # ARSoft.Tools.Net resolvers, cache, per-rule isolation
├── Routing/                    # Site routing, wildcard matching
├── Auth/                       # Client auth (IP/CIDR), per-client policy resolution
├── Throttling/                 # Token-bucket rate limit, connection limit, traffic quota
├── Persistence/                # SQLite traffic storage
├── Api/                        # Management REST API + auth middleware
└── Infrastructure/             # IP utilities, connection registry
```

## Dependencies

| Package | Purpose |
|---------|---------|
| [ARSoft.Tools.Net](https://github.com/alexreinert/ARSoft.Tools.Net) | DNS client (UDP/TCP/DoH) |
| [IPNetwork2](https://github.com/lduchosal/ipnetwork) | IP/CIDR matching |
| Microsoft.Data.Sqlite | Traffic persistence |

## License

MIT
