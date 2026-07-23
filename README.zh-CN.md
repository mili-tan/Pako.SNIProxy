# Pako.SNIProxy

[English](./README.md) | 简体中文

一个高性能、可投入生产的 C# **SNI 透明代理**（.NET 10）。工作在 TLS 握手层，解析 ClientHello 中的 SNI 字段进行路由，全程**不解密、不终止 TLS**，保持端到端加密与证书校验。

## 特性

- **SNI 透明代理**：解析 TLS ClientHello 的 SNI，直连源站并双向转发加密流量
- **自动域名解析**：支持系统 DNS / 自定义 UDP DNS / DoH（DNS over HTTPS），带 TTL 缓存
- **按站点指定 DNS**：不同站点可使用不同 DNS 服务器（如内网走内网 DNS、境外走 DoH）
- **锁定 IP**：站点可跳过 DNS 解析，直连固定 `IP:Port`
- **站点白名单 / 全放行**：默认白名单模式，支持通配符（`*.example.com`）
- **客户端鉴权**：IP / CIDR 白名单（基于 [IPNetwork2](https://github.com/lduchosal/ipnetwork)），默认允许任意 IP
- **每客户端策略**：针对单个 IP / 网段单独调整限速与流量配额，未匹配者回退全局默认
- **限速**：令牌桶算法，按客户端 IP 限速
- **并发连接限制**：按客户端 IP 与全局总量限制
- **流量配额**：按客户端 IP 的每日 / 每月流量上限，SQLite 持久化
- **管理 REST API**：运行时查看状态、增删白名单 / 客户端策略、查询流量、强制断开连接
- **低内存友好**：有界 DNS 缓存、空闲计数器淘汰、SQLite WAL 检查点，适合 1C1G VPS

## 工作原理

```
客户端 TCP 连接
  ├─ [1] 客户端鉴权 (IP/CIDR)            不通过 → 关闭
  ├─ [2] 并发连接数检查                  超限 → 关闭
  ├─ [3] 流量配额检查 (日/月)            超限 → 关闭
  ├─ [4] 读取 TLS ClientHello, 解析 SNI  失败 → 关闭
  ├─ [5] 站点路由决策
  │       ├─ 命中 PinnedEndpoint → 直连固定 IP
  │       ├─ Whitelist 模式未命中 → 拒绝
  │       └─ 命中规则/AllowAll → 选用对应 DNS 配置
  ├─ [6] DNS 解析 (缓存 → 配置的 DNS)    失败 → 关闭
  ├─ [7] 连接源站 (默认 443)             失败 → 关闭
  ├─ [8] 转发已读取的 ClientHello
  └─ [9] 双向 Relay (经限速器 + 流量计)  任一端关闭/超时 → 关闭双端
```

由于代理只转发加密字节流，客户端校验的是**源站真实证书**，证书验证正常生效（无需跳过校验）。

## 环境要求

- .NET 10 SDK
- Linux（推荐）/ Windows
- 监听 443 端口需要 root / CAP_NET_BIND_SERVICE

## 构建与运行

```bash
cd Pako.SNIProxy
dotnet build -c Release
dotnet run -c Release
# 或发布为独立可执行文件
dotnet publish -c Release -r linux-x64 --self-contained false -o ./publish
```

默认监听 `443`（SNI 代理）与 `127.0.0.1:9090`（管理 API）。

## 配置说明 (appsettings.json)

所有配置位于 `SniProxy` 节。

### 基础

| 字段 | 默认 | 说明 |
|------|------|------|
| `ListenPort` | `443` | SNI 代理监听端口 |
| `ListenAddress` | `"::"` | 监听地址（`::` 为 IPv4/IPv6 双栈） |
| `RouteMode` | `"Whitelist"` | `Whitelist`（白名单）或 `AllowAll`（全放行） |
| `DnsCacheMaxEntries` | `10000` | DNS 缓存最大条目数（超出按过期时间淘汰） |

### 全局 DNS (`Dns`)

| 字段 | 说明 |
|------|------|
| `Mode` | `System` / `Udp` / `DoH` |
| `Servers` | UDP: `["223.5.5.5:53"]`；DoH: `["https://dns.alidns.com/dns-query"]` |
| `CacheTtlSeconds` | 缓存 TTL 上限（实际取 DNS 返回 TTL 与该值的较小者） |
| `TimeoutMs` | 解析超时 |

### 站点规则 (`SiteRules`)

按顺序匹配，首条命中即生效。

```jsonc
"SiteRules": [
  { "Pattern": "*.internal.corp", "Dns": { "Mode": "Udp", "Servers": ["10.0.0.1:53"] } },
  { "Pattern": "*.baidu.com",     "Dns": { "Mode": "Udp", "Servers": ["223.5.5.5:53"] } },
  { "Pattern": "*.google.com",    "Dns": { "Mode": "DoH", "Servers": ["https://dns.alidns.com/dns-query"] } },
  { "Pattern": "example.com",     "PinnedEndpoint": "93.184.216.34:443" },
  { "Pattern": "*.github.com" }   // 未指定 Dns → 使用全局 DNS
]
```

- `Pattern`：精确域名或通配符 `*.example.com`（匹配子域名，不含 `example.com` 本身）
- `Dns`：该站点专属 DNS（省略则用全局 `Dns`）
- `PinnedEndpoint`：锁定 `IP:Port`，跳过 DNS 解析

### 客户端鉴权 (`ClientAuth`)

```jsonc
"ClientAuth": {
  "AllowAll": true,                          // true=允许任意IP; false=仅白名单
  "Whitelist": ["192.168.1.0/24", "10.0.0.1", "fd00::/64"]
}
```

### 全局限速 (`RateLimit`) / 连接限制 (`ConnectionLimit`) / 流量配额 (`TrafficQuota`)

```jsonc
"RateLimit":       { "Enabled": true, "BytesPerSecond": 10485760, "BurstBytes": 20971520 },
"ConnectionLimit": { "Enabled": true, "MaxPerClientIp": 50, "MaxTotal": 1000 },
"TrafficQuota":    { "Enabled": true, "DailyLimitBytes": 10737418240, "MonthlyLimitBytes": 214748364800, "PersistPath": "./data/traffic.db" }
```

### 每客户端策略 (`ClientRules`)

针对单个 IP / 网段覆盖限速与配额。**按顺序匹配，首条命中生效**；未匹配的客户端使用上面的全局默认。规则内未设置的字段同样回退全局默认。

```jsonc
"ClientRules": [
  {
    "Pattern": "192.168.1.100",                       // 单 IP
    "RateLimit": { "Enabled": true, "BytesPerSecond": 1048576, "BurstBytes": 2097152 },
    "TrafficQuota": { "DailyLimitBytes": 1073741824, "MonthlyLimitBytes": 10737418240 }
  },
  {
    "Pattern": "10.0.0.0/8",                          // 网段
    "RateLimit": { "Enabled": false },                // 该网段不限速
    "TrafficQuota": { "Enabled": false }              // 该网段不计配额
  }
]
```

- `RateLimit`：省略 → 用全局；`Enabled:false` → 该客户端不限速
- `TrafficQuota`：`Enabled` / `DailyLimitBytes` / `MonthlyLimitBytes` 均可选，省略的字段回退全局

> 用 `GET /api/client-rules/resolve?ip=<ip>` 可查看任意 IP 最终生效的策略（含回退结果）。

### 管理 API (`ManagementApi`)

```jsonc
"ManagementApi": { "Enabled": true, "ListenPort": 9090, "ListenAddress": "127.0.0.1", "AuthToken": "改成强随机密钥" }
```

> 生产环境务必修改 `AuthToken`，并保持 `ListenAddress` 为回环地址或通过防火墙限制访问。

### 连接参数 (`Connection`)

| 字段 | 默认 | 说明 |
|------|------|------|
| `InitialReadTimeoutMs` | `5000` | 读取 ClientHello 超时 |
| `ConnectTimeoutMs` | `5000` | 连接源站超时 |
| `IdleTimeoutMs` | `300000` | 无数据传输的空闲超时 |
| `BufferSizeBytes` | `16384` | 每方向转发缓冲区（增大可提升吞吐，但增加内存） |
| `Backlog` | `1024` | 监听队列长度 |

## 管理 API

所有端点需携带 `Authorization: Bearer <AuthToken>`。

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/status` | GET | 运行状态、连接数、内存、uptime |
| `/api/whitelist/sites` | GET/POST | 站点白名单查询 / 增改 |
| `/api/whitelist/sites/{pattern}` | DELETE | 删除站点规则 |
| `/api/whitelist/clients` | GET/POST | 客户端白名单查询 / 增加 |
| `/api/whitelist/clients/{entry}` | DELETE | 删除客户端白名单条目 |
| `/api/whitelist/clients/allow-all` | PUT | 设置 `{"allowAll":true/false}` |
| `/api/client-rules` | GET/POST | 客户端策略查询 / 增改 |
| `/api/client-rules/{pattern}` | DELETE | 删除客户端策略 |
| `/api/client-rules/resolve?ip=` | GET | 查询某 IP 生效策略（含回退） |
| `/api/traffic` | GET | 各客户端流量统计 |
| `/api/traffic?ip=` | GET | 指定 IP 的日/月流量 |
| `/api/traffic/reset?ip=` | POST | 重置指定 IP 流量计数 |
| `/api/connections` | GET | 当前活跃连接 |
| `/api/connections/{id}` | DELETE | 强制断开连接 |
| `/api/config/route-mode` | GET/PUT | 查询 / 切换路由模式 |
| `/api/dns/cache` | GET/DELETE | 查询 / 清空 DNS 缓存 |

示例：

```bash
TOKEN="你的密钥"
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:9090/api/status
curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"pattern":"*.example.com"}' http://127.0.0.1:9090/api/whitelist/sites
```

## 透明代理部署

SNI 代理本身只负责按 SNI 路由，需要配合网络层把目标流量导入代理端口。

### 方式一：DNS 劫持（网关 / 路由器）

将客户端 DNS 指向运行在本机的 DNS 服务（如 dnsmasq），把白名单域名解析到代理 IP，客户端访问时流量自然到达代理的 443 端口。

### 方式二：iptables 重定向（本机透明代理）

```bash
# 将本机发出的 443 流量重定向到本地 8443（代理监听 8443 时）
iptables -t nat -A OUTPUT -p tcp --dport 443 -j REDIRECT --to-port 8443
```

### 方式三：nftables

```bash
nft add rule ip nat output tcp dport 443 redirect to :8443
```

> 注意：代理监听端口需与重定向目标一致；若代理直接监听 443 则无需重定向（需 root）。

## 性能与内存调优

默认值已针对低配 VPS 做了保守设置。运行时内存主要来自四个来源：每连接转发缓冲区、DNS 缓存、流量计数器、SQLite。

| 关注点 | 默认 | 说明 |
|--------|------|------|
| 每连接缓冲区 | `BufferSizeBytes=16384` | 每连接约 `2 × BufferSizeBytes`（每个方向一个缓冲区）。提高吞吐可调到 32768/65536，但内存随之上升 |
| 最大连接数 | `MaxTotal=1000` | 每连接还占用 2 个 socket + 一个空闲看门狗定时器 |
| 单 IP 连接数 | `MaxPerClientIp=50` | 防止单客户端耗尽资源 |
| DNS 缓存 | `DnsCacheMaxEntries=10000` | 有界，超出按过期时间淘汰，避免无限增长 |
| 流量计数器 | 自动淘汰 | 空闲超过 1 小时的客户端计数器在刷盘后从内存移除 |
| SQLite | WAL + 定期 checkpoint | 每 10 分钟 `wal_checkpoint(TRUNCATE)` 并清理 60 天前记录 |

**内存估算经验公式：**

```
缓冲区内存 ≈ MaxTotal × 2 × BufferSizeBytes
```

此外还需为 .NET 运行时、DNS 缓存与 SQLite 预留约 80–120 MB。用两者之和来设置 `MemoryMax`。

### 推荐配置参考

| 规格 | MaxPerClientIp | MaxTotal | BufferSizeBytes | Backlog | DnsCacheMaxEntries | systemd `MemoryMax` |
|------|---------------|----------|-----------------|---------|--------------------|---------------------|
| **1C1G** | 30 | 500 | 16384 | 512 | 5000 | 512M |
| **2C2G** | 50 | 1500 | 32768 | 1024 | 10000 | 1G |
| **2C4G+** | 100 | 5000 | 65536 | 2048 | 50000 | 2G |

<details>
<summary><b>1C1G</b> — 1 核 / 1 GB</summary>

```jsonc
"ConnectionLimit":    { "Enabled": true, "MaxPerClientIp": 30,  "MaxTotal": 500 },
"Connection":         { "BufferSizeBytes": 16384, "Backlog": 512 },
"DnsCacheMaxEntries": 5000
```

</details>

<details>
<summary><b>2C2G</b> — 2 核 / 2 GB</summary>

```jsonc
"ConnectionLimit":    { "Enabled": true, "MaxPerClientIp": 50,  "MaxTotal": 1500 },
"Connection":         { "BufferSizeBytes": 32768, "Backlog": 1024 },
"DnsCacheMaxEntries": 10000
```

</details>

<details>
<summary><b>2C4G+</b> — 2 核以上 / 4 GB+</summary>

```jsonc
"ConnectionLimit":    { "Enabled": true, "MaxPerClientIp": 100, "MaxTotal": 5000 },
"Connection":         { "BufferSizeBytes": 65536, "Backlog": 2048 },
"DnsCacheMaxEntries": 50000
```

</details>

其他建议：

- 用 systemd 管理进程并设置 `MemoryMax` 作为兜底（见上表）
- `IdleTimeoutMs` 不宜过大，及时回收空闲连接
- 日志级别保持 `Information`，避免在高频路径开启 `Debug`
- 转发为异步 I/O 密集，多核主要在高连接数场景下更有优势

## 安全建议

- 修改 `ManagementApi.AuthToken` 为强随机值，管理端口仅绑定回环地址
- 公开部署时建议启用 `ClientAuth.AllowAll=false` 并配置白名单
- 按需启用限速与流量配额，防止滥用
- 锁定 IP（`PinnedEndpoint`）可规避 DNS 污染，但需自行维护 IP 有效性

## 项目结构

```
Pako.SNIProxy/
├── Program.cs                  # 入口、DI 编排、Kestrel(管理API)
├── appsettings.json            # 配置
├── Configuration/              # 强类型配置 + 校验
├── Core/                       # SNI 解析、连接转发、监听服务、连接上下文
├── Dns/                        # ARSoft.Tools.Net 解析器、缓存、按规则隔离
├── Routing/                    # 站点路由、通配符匹配
├── Auth/                       # 客户端鉴权(IP/CIDR)、每客户端策略解析
├── Throttling/                 # 令牌桶限速、连接限制、流量配额
├── Persistence/                # SQLite 流量存储
├── Api/                        # 管理 REST API + 鉴权中间件
└── Infrastructure/             # IP 工具、连接注册表
```

## 依赖

| 包 | 用途 |
|----|------|
| [ARSoft.Tools.Net](https://github.com/alexreinert/ARSoft.Tools.Net) | DNS 客户端（UDP/TCP/DoH） |
| [IPNetwork2](https://github.com/lduchosal/ipnetwork) | IP/CIDR 匹配 |
| Microsoft.Data.Sqlite | 流量持久化 |

## License

MIT
