# 从 Go 迁移到 ASP.NET Core —— 迁移计划

> 🚩 **方向已变更（2026-09-19）：放弃与 Go 的逐字节兼容，改用原生 ASP.NET Core。**
> 本文下面的很多章节是在「逐字节对齐 Go」这个前提下写的，现在**只有历史价值**。
> 变更的原因、保留了什么、哪些章节作废，见紧接着的 §0.0。**先读它再读别的。**
>
> 本文档中的 Kestrel / .NET 默认值均为**在本机 .NET 10.0.12 上实测得出**，不是文档抄录。

---

## 0.0 🚩 方向变更：不再为兼容 Go 而写 C#

### 变更前的前提

最初的验收标准是**「和 Go 逐字节一致」**。这不是需求方提出的，是**迁移执行者自己设的**，理由是可以让两个服务并排跑、机械 diff、随时回滚——作为**迁移期的安全网**是合理的，但被当成了**目标本身**。

结果是 `dotnet/` 里出现了 `GoJson`、`GoPath`、`GoMime`、`GoDuration` 这类以来源而不是以职责命名的文件，以及一批「因为 Go 这么做」的注释。这被正确地指了出来。

### 为什么这个前提站不住

**1. 它没有换来性能，因为它根本不在热路径上。** 实测各文件的位置：

| 文件 | 实际被谁调用 | 频率 |
|---|---|---|
| `GoJson` | 5 个 REST 端点 + 错误信封 | 每页面加载个位数次 |
| `GoDuration` | 启动日志、流的开场/收尾摘要 | 每流几次 |
| `GoMime` | 静态资源 | 每资源一次 |
| `GoPath` | 每请求一次、WS 握手一次 | 每请求一次 |

而真正每帧执行的是 `Nsst.Core` 的 `FrameEncoder.TryAppend` → `ByteWriter` → `PayloadDocument.AppendEscapedSlice`，写进调用方复用的缓冲区，**零分配**。这个文件从来就不叫 `Go*`，它是产品自己的帧格式。

更关键的是数量级：间隔下限 10ms ⇒ 单流最多 100 帧/秒，100 路并发 ⇒ 全服务 1 万帧/秒。**这个负载根本不是吞吐瓶颈**。这是个**测量抖动**的工具，决定它好不好用的是**定时精度和 GC 停顿**，不是 JSON 库。

所以「换成原生 ASP.NET + 第三方库能更快」这个前提是错的。反而是把 `System.Text.Json` 用在 REST 上比手写 writer **更慢**（STJ 要分配；源生成只是把差距补回来）。

**2. 有几处「按 Go 写」的东西其实是正确性，不是兼容性。** 丢掉就是功能退化，所以**无论方向怎么变都保留**：

- 读接口接受 `HEAD`（原生 ASP.NET 的 `MapGet` 不匹配 HEAD，会回 405）；
- `/api/stream/http/` 这类**带尾部斜杠的流路径返回 405，而不是真的开一条流**——原生路由会把它当成同一个路由，一条拼错的 URL 就能占掉一个并发槽位；
- 畸形 WebSocket 握手返回 **426/400 加可读的原因**，而不是 Kestrel 吞掉异常后留下的**空 200**。

**3. 前端是唯一的消费者，而它对字节不敏感。** 实测（`web/src/*.ts`）：所有响应都过 `JSON.parse` 或按键取值，因此 **JSON 转义方式和键顺序可以随便变**；真正不能变的是**字段名、类型与单位**（`uptime_seconds` 是整数秒、`max_duration` 是秒、`min_interval` 是毫秒、`server_time` 是 Unix 毫秒、`proxy`/`tls` 是布尔）。

### 变更后：做了什么

| 项 | 之前 | 现在 |
|---|---|---|
| REST 序列化 | 手写 `GoJson`，复刻 Go 的转义与键序 | **`System.Text.Json` + 源生成** `ApiJsonContext`，DTO 上的 `[JsonPropertyName]` 就是契约 |
| 跨域 | 手写 `CorsMiddleware` | **原生 `AddCors` / `UseCors`**，默认策略 |
| 静态资源 MIME | 手写 `GoMime` 表 | 框架自带 **`FileExtensionContentTypeProvider`** + 文本类型补 charset |
| 路径规范化 | 复刻 `cleanPath` + 307 重定向 | **删掉**。只留「`/api/` 下的尾部斜杠 → 405」这一条正确性护栏 |
| 时长格式 | `GoDuration` | `DurationText`（职责命名；格式保留，它只进日志和摘要文案） |
| 头部分词匹配 | `GoPath.HasToken` | `HeaderTokens.Contains` |
| 逐字节对比工具 | `dotnet/parity/*.ps1`（3 个，依赖 Go 服务器） | **已删除**（见下） |

### 哪些东西留下来了，以及为什么

- **帧格式**（`sequence` / `server_time` / `payload_size` / `payload`，以及 `payload_size == 0` 时不出现 `payload`）：**前端在解析它**，这是真契约。保护它的是 `Nsst.Core.Tests` 里的 golden 用例（`golden/frames.txt`、`golden/params.txt`），它们不依赖 Go 运行时。
- **SSE 的 `id:` + `event: data` 帧**、**WebSocket 必须发文本帧**：同上，前端在解析。
- **上面第 2 点的三条正确性行为**。
- **`GoDuration` 的格式本身**：它出现在流的开场/收尾摘要文案里，改格式属于无谓的破坏。文件改了名，理由改成「给人看的紧凑时长」。

### 被删掉的东西，以及为什么删

`dotnet/parity/` 下的三个脚本**唯一的作用**就是逐字节对比 Go 与 C#。前提没了，它们就会永久红灯。留着比删掉更糟——它会训练人忽略失败。已删除。

**代价要说清楚：并行 diff 这个安全网没有了。** 从现在起，C# 的正确性由两条防线保证：`dotnet test`（**134 个用例**，含帧与参数的 golden 对比）和前端契约（§0.0 表格里的字段名/类型/单位）。**切换 Go → C# 不再有「两边跑一遍看是否一致」这一步；而且切换完成后 Go 树已被整体删除（见 §0.0.1），所以也不再保留 Go 二进制作为回滚路径。**

### 关于性能：真正该动的地方

顺带纠正一个我先前的误判。我原以为「GC 没配」是个大漏洞，实测 `Nsst.Server.runtimeconfig.json` 后：**`System.GC.Server` 已经是 `true`**——Web SDK 默认就开服务器 GC，后台（并发）GC 随之而来。所以这里没有漏掉的开关。

现在做的是**把这三个默认值显式钉住**（`Directory.Build.props`），因为它们对本项目是**正确性级别**的：这个工具测的是帧间隔抖动，一次阻塞式 gen2 回收会被当成网络的锅报出去。钉住之后，SDK 未来改默认值也不会把这份误差悄悄挪进产品。

```
<ServerGarbageCollection>true</ServerGarbageCollection>
<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
<TieredPGO>true</TieredPGO>
```

**真正还没做的性能工作是 §7.2 的验收**（三协议并发、60 秒、p99/p99.9、服务端 CPU/内存/GC）——那才是「最好的性能」该被证明的地方，而不是换 JSON 库。

---

## 0.0.1 切换已完成：Go 树已删除

**本节是现状，不是计划。** §0.0 的方向变更已经执行完毕：

- **Go 实现已从仓库删除** —— `cmd/`、`internal/`、`pkg/`、`go.mod`、`go.sum`、`bin/`，以及生成 golden 夹具的 `dotnet/golden/` 和 `dotnet/parity/` 对比脚本，全部移除。Go 工具链不再参与任何构建或测试。
- **C# 服务是唯一的实现** —— `dotnet/src/Nsst.Core` + `dotnet/src/Nsst.Server`；`Directory.Build.props` 开着 `TreatWarningsAsErrors`，构建 **0 警告 0 错误**；`dotnet test` **134 个用例全绿**（Core 53 + Server 81）。
- **内嵌正文搬家** —— `pkg/protocol/payload.txt` → **`dotnet/src/Nsst.Core/Protocol/payload.txt`**（8699 字节，纯 ASCII），以 `EmbeddedResource` 嵌进 `Nsst.Core`。现在是**唯一一份**。
- **前端产物搬家** —— `internal/webui/dist` → **`web/dist`**（已被 `.gitignore` 忽略），由 `web/` 里的 `npm run build` 产出，服务端构建时把它内嵌。**构建服务端之前必须先构建前端**，否则嵌进去的是上一次的产物或空目录。
- **版本注入已接通** —— 版本号只在 `dotnet/Directory.Build.props` 里声明一次（当前 `0.2.2`）；`ServerConfig.ServiceVersion` 在运行时从程序集的 `AssemblyInformationalVersion` 读取，并剥掉 SDK 追加的 `+<sha>` 构建元数据。流水线用 `-p:Version=` 覆盖，**Dockerfile 就是这么做的**，C# 里没有第二份会跟它不一致的版本号。
- **构建链路已补齐** —— `Dockerfile`（web → sdk → runtime 三段式）和重写后的 `Makefile` 都在仓库里。Makefile 目标是 `help deps web build run test test-cover fmt fmt-check lint smoke smoke-full docker docker-run docker-smoke clean all`。

> ⚠️ **仍然没做完的两件事**，读后面各节时按这个前提读：
> - **.NET 容器镜像从未构建过**。本机 Docker 守护进程未运行，镜像体积与容器内行为都还没有实测（见 §9 第 10 项）。
> - **§7.2 的 pacing 验收仍未做**，而且它已经失去「和 Go 对比」这条路（见 §7.2 与 §9 第 9、6 项）。

---

## 0. 先说结论（⚠️ 以下为变更前的前提，部分已作废）

> **原定方向（已作废）：**
> - 运行时模型：**普通 ASP.NET Core + `net10.0`**，本阶段不使用 NativeAOT ← *仍然成立*
> - 落地方式：**`dotnet/` 目录并存 + 并行契约验证**，Go 代码在切换前保持不动 ← *「并行契约验证」已放弃；切换完成后 Go 代码已删除（§0.0.1）*
>
> 未决项见 §9。

**能力核对（详见 §8）：** 迁移会丢失的 10 项能力里，5 项直接等价或纯赚，4 项能自己补回来（其中 2 项补得比 Go 更好），**只有 1 项补不回来 —— `go test -race` 没有等价物**。而本项目的共享可变状态只有 3 处、可枚举、可通过构造保证正确。

1. **技术上可行，没有阻塞项**，但有三个必须先验证的点（见 §2）。其中逐帧写超时（§2.2）已确认可以补齐，且能比 Go 更精确地分类。
2. **不要用 NativeAOT 起步**。NativeAOT 会摧毁当前"构建一次、原生交叉编译"的多架构策略（见 §6 补充），而且会把语言迁移和 AOT 兼容性两个风险叠在一起。
3. **镜像会变大**。当前是 21.3 MB；换成 `aspnet:10.0-alpine` + 框架依赖发布后，预计是它的 2～3 倍。**具体数字待 Phase 0 Spike B 实测**，本文不预先断言。（**该 Spike 至今未执行**——Docker 守护进程始终未运行，见 §9 第 10 项；21.3 MB 是当时 Go 镜像的实测值，现已无法复现。）
4. **迁移成本主要在测试，不在代码**。生产代码约 2,600 行，测试 89 个函数 / 12 个文件，而且这些测试**就是这个项目的规格说明书**。
5. **真正的验收标准不是"能跑"，而是"pacing 不能变差"**。这个工具的全部价值在于测量帧间隔稳定性；如果 C# 版在 GC 停顿下把帧间隔抖动做大了，那这次迁移就是负收益（见 §7.2）。

---

## 1. 现状盘点（要迁移的东西）

> 🚩 **已作废（盘点对象已不存在）**：本节与 §1.1 的表格盘点的是**当时的 Go 树**，保留它是为了说明迁移工作量的依据。`cmd/`、`internal/`、`pkg/` 已于切换时整体删除（见 §0.0.1），下表列出的路径**现在都不在仓库里**。

### 1.1 生产代码

| Go 包 | 行数 | 职责 | 迁移难度 |
|---|---:|---|---|
| `cmd/server/main.go` | 192 | 启动、信号、优雅关闭 | 中（关闭时序是难点） |
| `internal/api/api.go` + `client.go` | 311 | 路由、5 个 REST 端点 | 低 |
| `internal/config/config.go` | 186 | 环境变量加载/校验/夹取 | 低 |
| `internal/stream/limits.go` | 183 | 参数解析与校验 | 低（**错误文案必须一致**） |
| `internal/stream/manager.go` | 173 | 配额租约、排空、强制关闭 | 中高 |
| `internal/stream/stream.go` | 133 | 发帧主循环、结束原因分类 | 中高（取消原因模型不同） |
| `internal/stream/pacer.go` | 67 | 绝对时间网格、迟到重对齐 | 低（算法纯逻辑） |
| `internal/stream/report.go` | 53 | 结构化日志 | 低 |
| `internal/httpstream/handler.go` | 110 | NDJSON | 中（逐帧 flush + 写超时） |
| `internal/sse/handler.go` | 149 | SSE | 中（同上 + 心跳） |
| `internal/websocket/handler.go` | 156 | WebSocket | 高（关闭握手、断开感知） |
| `internal/httpx/httpx.go` | 243 | 错误信封、CORS、panic 恢复 | 中（**不能直接用 `UseCors`**） |
| `internal/httpx/clientip.go` | 109 | 代理头解析与归一化 | 低 |
| `internal/metrics/metrics.go` | 48 | 无锁原子计数器 | 低 |
| `internal/webui/webui.go` | 68 | 内嵌前端 + SPA 回退 | 中（embed → EmbeddedResource） |
| `pkg/protocol/protocol.go` | 162 | 帧编码器（零分配） | **高**（字节级一致） |
| `pkg/protocol/payload.go` | 234 | 正文加载/切分/JSON 转义 | **高**（rune 对齐 + 转义集） |
| **合计** | **≈ 2,576** | | |

### 1.2 不可谈判的对外契约

> 🚩 **部分作废：这里的「逐字节」要求已按 §0.0 撤销。** 仍然不可谈判的只有两类：**前端真正解析的东西**（帧字段名/类型/单位、SSE 帧、WebSocket 文本帧）和 **§0.0 记录的三条正确性行为**（`HEAD` 返回 200、`/api/stream/*/` 尾部斜杠返回 405、畸形 WS 握手返回 426/400）。下面清单里的响应头、状态码映射、pacing 语义仍然有效；凡是「与 Go 逐字节一致」的部分不再作为验收项，且已没有对照物可以重跑。

迁移必须**逐字节**保持的东西：

- 帧 JSON 字段名与顺序：`sequence`、`server_time`、`payload_size`、`payload`
- `payload_size == 0` 时**不出现** `payload` 字段
- 错误信封：`{"error":...,"message":...,"param":...}`，以及 400/404/405/503 的映射
- `Allow` 头、`Retry-After: 5`（超额）/`Retry-After: 10`（关闭中）
- 响应头：`application/x-ndjson`、`text/event-stream`、`Cache-Control`、`X-Accel-Buffering: no`
- SSE 开场注释 `: stream open\n\n`、15 秒 `: keep-alive` 心跳
- WebSocket 关闭码 `1000`/`1001`/`1011` 及原因文案
- `/api/metrics` 的 8 个字段名、`/api/config` 与 `/api/info` 的结构
- 并发超限在**升级之前**返回 HTTP 503 JSON，而不是升级后再发关闭帧
- 环境变量名、默认值、非法值**启动即失败**
- pacing 语义：**绝对网格，迟到超过一个间隔就重对齐，绝不补发突发**

---

## 2. 关键实测发现（全部实测，逐条可复现）

用本机 .NET 10.0.12 直接读取了 `KestrelServerLimits` 与 `HostOptions` 的运行期默认值。

### 2.1 ⚠️ Kestrel 默认会在 5 秒后掐死稀疏流

```
MinResponseDataRate      = 240 bytes/s, grace 00:00:05
```

Kestrel 默认要求响应体速率不低于 **240 B/s**，宽限 5 秒，低于就**直接中止连接**。

算一下本项目的典型请求：

| 请求 | 每帧字节 | 实际速率 | 结果 |
|---|---:|---:|---|
| `payload_size=0&interval=250ms` | ≈ 50 B | ≈ 200 B/s | **< 240，5 秒后被掐** |
| `payload_size=0&interval=1000ms` | ≈ 50 B | 50 B/s | **必被掐** |
| `payload_size=80&interval=100ms` | ≈ 130 B | ≈ 1.3 KB/s | 安全 |

而 `payload_size=0` 是 **README 里明确记录的合法用法**（`/api/stream/sse?...&payload_size=0`，CI 冒烟测试也在用）。

**必须显式关闭**，否则 CI 冒烟测试会以"SSE 只有 1 个 event"的形式失败，而且原因极难定位：

```csharp
builder.WebHost.ConfigureKestrel(o =>
{
    // 长连接流式端点的速率由 interval 决定，可能远低于 Kestrel 的默认下限。
    o.Limits.MinResponseDataRate = null;
    o.Limits.MinRequestBodyDataRate = null;   // 本服务不接收请求体
});
```

### 2.2 ⚠️ 逐帧写超时只能靠 Kestrel 的连接级定时器近似

Go 用的是 `http.ResponseController.SetWriteDeadline` —— 每帧单独设写超时，客户端不读就断开。

ASP.NET Core / Kestrel **没有等价的公开 API**（`IHttpResponseBodyFeature` 只有 `Stream`、`Writer`、`DisableBuffering`、`CompleteAsync`，没有 deadline）。但实测确认 Kestrel 暴露了：

```csharp
Microsoft.AspNetCore.Server.Kestrel.Core.Features.IConnectionTimeoutFeature
    Void SetTimeout(TimeSpan timeSpan)
    Void ResetTimeout(TimeSpan timeSpan)
    Void CancelTimeout()
```

这是连接级定时器，超时未重置则**中止整条连接**。可以这样逼近"逐帧写超时"：

```csharp
// 只在写这一帧的窗口内武装定时器；pacing 等待期间必须拆掉，
// 否则 interval > WRITE_TIMEOUT_MS 的合法请求会被误杀
// （MAX_INTERVAL 默认 60000ms，WRITE_TIMEOUT_MS 默认 15000ms）。
var timeouts = context.Features.Get<IConnectionTimeoutFeature>();
timeouts?.SetTimeout(_limits.WriteTimeout);
try
{
    await body.Writer.WriteAsync(frame, ct);
    await body.Writer.FlushAsync(ct);
}
finally
{
    timeouts?.CancelTimeout();
}
```

**语义差异（必须接受）：**
- Go：一次 `Write` 返回错误，`EndReason = write_error`，流被分类为"写失败"。
- Kestrel：连接被中止，主循环观察到的是**取消**，会落进 `EndClientClosed` 而不是 `EndWriteError`。

这会影响 `/api/metrics` 里 `streams_errored` 与 `streams_cancelled` 的分布，也会改变 `closeCode()` 选出的 WebSocket 关闭码。三种处理方案：

| 方案 | 做法 | 取舍 |
|---|---|---|
| A（推荐） | 用上面的 `IConnectionTimeoutFeature`，并把"连接被中止"识别为 `EndWriteError` | 需要可靠区分"对端关闭"与"定时器中止"，建议自建一个 `CancellationTokenSource` 配合 |
| B | 依赖 `MinResponseDataRate`（设成 `WriteTimeout` 对应的速率） | 粒度粗、5 秒宽限、且与 §2.1 冲突 |
| C | 流式端点绕开 Kestrel，自己用 `Socket` + `NetworkStream.WriteTimeout` | 语义最接近 Go，但等于放弃 ASP.NET Core 的托管管线，不推荐 |

**这条必须先用 Spike 验证（§3 Phase 0-A）。**

> **后续更正：** 方案 A 的具体写法见 **§8.3（1）**。用 `CancelAfter` + 异常过滤器
> `when (!ct.IsCancellationRequested)` 可以**干净地区分**"写超时"与"对端断开"，
> 因此上面担心的"指标口径漂移"其实**可以避免**。本项风险等级由「高」下调为「中」，
> 不再是被迫接受的妥协。§8 是本节结论的修订版，评审时以 §8 为准。

### 2.3 ⚠️ `HostOptions.ShutdownTimeout` 默认 30 秒 > 容器 20 秒宽限

```
HostOptions.ShutdownTimeout = 00:00:30
```

而 `docker-compose.yml` 里 `stop_grace_period: 20s`，`SHUTDOWN_TIMEOUT: 15`。

- Go 现状：排空最多 15 秒 + 关闭套接字最多 5 秒 = 20 秒，**正好顶到** `stop_grace_period`（这本身已经是个隐患，README 也声称退出码为 0）。
- 换到 .NET：默认 30 秒 > 20 秒，Docker 会在排空完成前 `SIGKILL`，**退出码变成 137**，README 里"优雅退出、退出码 0"的实测结论会失效。

必须显式配置并把三个数字排成一致关系：

```
SHUTDOWN_TIMEOUT(15s) + closeTimeout(5s) ≤ HostOptions.ShutdownTimeout < stop_grace_period
```

建议：`HostOptions.ShutdownTimeout = 18s`，`stop_grace_period` 提到 `25s`。

### 2.4 ⚠️ JSON 转义集不一致（**实测已暴露**，不是理论风险）

这里有**两条不同**的转义路径，初版设计把它们混为一谈了，实测后已修正。

**路径一：帧正文。** `pkg/protocol/payload.go` 的 `appendEscaped` **不做 HTML 转义**，只处理 `"`、`\`、`\n`、`\r`、`\t` 和其余 `<0x20`。而 Go 的 `encoding/json` 默认会把 `<`、`>`、`&` 转成 `\u003c` 等。

实测内嵌正文 `pkg/protocol/payload.txt`（**该文件现已迁至 `dotnet/src/Nsst.Core/Protocol/payload.txt`，见 §0.0.1**；本节记的是当时的路径与实测数值）：

```
SHA256 : D00718988BA3C1276CEC677900BEBE08F620D76E06396BA7055C384DED53F9BA
Bytes  : 8699      纯 ASCII
可转义字节：恰好 35 个，全部是 \n
& = 0    < = 0    > = 0    \ = 0    " = 0    \r = 0    \t = 0
```

所以帧的字节级比对能过，**是因为内嵌正文恰好不含这些字符**，不是因为两条路径规则一致。C# 的帧编码器（`PayloadDocument.AppendEscapedSlice`）复刻的是 `appendEscaped`，在该路径上两边规则确实相同——975 条金标准用例逐字节通过可以佐证。

**路径二：REST 正文。** 这条走 Go 的 `encoding/json`，**会**做 HTML 转义。把两个服务器并排跑起来逐字节比对，第一次就抓到真实差异（不是推测）：

| 场景 | Go | C#（System.Text.Json 默认） |
| --- | --- | --- |
| `duration=0` 的错误正文 | `parameter \"duration\": ...` | `parameter \u0022duration\u0022: ...` |
| `user_agent` 含 `a<b&c` | `a\u003cb\u0026c` | `a\u003Cb\u0026c` |

两个内置编码器**都不能用**：

| 编码器 | 问题 |
| --- | --- |
| `JavaScriptEncoder.Default` | `"` 写成 `\u0022`，Go 写 `\"` |
| `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` | `"` 对了，但不再转义 `<`、`>`、`&`，而 Go 转义 |

> 本文件上一版建议用 `UnsafeRelaxedJsonEscaping`，**那是错的**：它只是把差异从引号搬到了 `<>&`。教训是这种「差不多对」的编码器不能靠推理选，必须拿真实响应比。

**实际采用**：~~手写 `dotnet/src/Nsst.Server/Http/GoJson.cs`，逐字符复刻 `encoding/json` 的规则~~ —— **🚩 已作废，见 §0.0。** 该文件已删除，REST 现在走 `System.Text.Json` 源生成。下面记录的是当时复刻出来的转义规则，保留仅供理解帧编码器（`Nsst.Core.Protocol.PayloadDocument`）为什么是那样写的：`"`→`\"`、`\`→`\\`、`\n`/`\r`/`\t`、其余 `<0x20`→`\u00xx`（**小写**十六进制）、`<`/`>`/`&`→`\u003c`/`\u003e`/`\u0026`、U+2028/U+2029、孤立代理项→`\ufffd`。顺带把键序也钉死，不再依赖反射顺序。

结果：**24 个 REST 对比用例 22 个逐字节一致**（另 2 个见 2.6 与 7.1）。

---

### 2.5 ⚠️ `MapFallback` 的隐式 `:nonfile` 约束吞掉了「缺资源」这一整类请求

`MapFallback(RequestDelegate)` 生成的模式是 `{*path:nonfile}`。`nonfile` 约束意味着**最后一段带点号的路径根本不参与匹配**：

```
GET /assets/index.js      → 匹配，正常走处理器
GET /assets/missing.css   → 不匹配 → 框架直接回 404，处理器一行都没执行
```

Go 的 `internal/webui` 对「有扩展名但文件不存在」的路径调 `http.NotFound`，回 `404 page not found\n`。C# 初版因此回了**空正文、无 Content-Type** 的 404——不是设计取舍，是路由约束把请求挡在了处理器外面。

修法是把模式显式写出来，绕开 `nonfile`：

```csharp
routes.MapFallback("/{**path}", ctx => ServeAsync(ctx, assets));
```

同一处还有两个必须照抄的细节：

- Go 的 webui 用的是 `http.Error`/`http.NotFound`，即**纯文本** + `X-Content-Type-Options: nosniff`，**不是** `/api/` 那套 JSON 错误体；
- 405 的正文是 `method not allowed\n`，并带 `Allow: GET, HEAD`。

这三点不照抄，`compare-rest.ps1` 的 `unknown asset`、`spa wrong method` 两条就会红。

---

### 2.6 ⚠️ Go 的静态资源 Content-Type 依赖宿主机，开发机与 Docker 不一致

Go 的 `mime.TypeByExtension` 先装载内置表 `builtinTypesLower`，再调 `osInitMime()` **覆盖**它：

- Windows：读 `HKEY_CLASSES_ROOT\.<ext>\Content Type`
- Unix：读 `/etc/mime.types`、`/usr/share/mime/globs2` 等（存在的话）

同一份 Go 二进制在不同机器上回的 `Content-Type` 就不一样。实测：

| 扩展名 | 本机 Go（Windows 注册表） | Go 内置表 |
| --- | --- | --- |
| `.js` | `application/javascript` | `text/javascript; charset=utf-8` |
| `.css` | `text/css; charset=utf-8` | `text/css; charset=utf-8` |
| `.html` | `text/html; charset=utf-8` | `text/html; charset=utf-8` |
| `.ico` | `image/x-icon` | `image/vnd.microsoft.icon` |

`.css`/`.html` 两边恰好一致，是因为注册表里存的是裸 `text/css`，而 Go 的 `setExtensionType` 会给所有缺 charset 的 `text/*` 补上 `; charset=utf-8`——**这条规则也是实测确认的**，不是读源码推的。

**生产环境走哪条？走内置表。** `Dockerfile` 的 runtime 阶段是裸 `alpine:3.21`，一个 `apk add` 都没有；而 alpine v3.21 的 `/etc/mime.types` 只由 `mailcap` 包提供（已核对 pkgs.alpinelinux.org 的 contents 索引），基础镜像里没有。所以 **Docker 里 `.js` 是 `text/javascript; charset=utf-8`**。

**结论**：~~C# 侧用固定表复刻 Go 的内置表（`dotnet/src/Nsst.Server/WebUi/GoMime.cs`）~~ —— **🚩 已作废，见 §0.0。** `GoMime.cs` 已删除，改用框架自带的 `FileExtensionContentTypeProvider`，只对 text/json/javascript 补 `charset=utf-8`。当前行为由 `WebUiEndpoints.ContentTypeFor` 决定，`WireFormatTests` 逐类型断言。当初的取舍记录是：对齐**生产**行为（开发机上跑 Go 会走 Windows 注册表报 `application/javascript`，而产品跑在 alpine 上没有 `/etc/mime.types`，报 `text/javascript`）——这个取舍本身仍然成立，只是实现换了。

Go 对未登记的扩展名会回退到嗅探前 512 字节（`http.DetectContentType`）。这一点**故意不实现**：半吊子嗅探会静默跑偏，比明确不做更糟。改为启动时遍历内嵌资源，发现表里没有的扩展名就 `LogWarning` 一条——把静默偏差变成显式告警。

### 2.7 🔴 .NET 的等待原语被量化到 15.6ms，pacer 的 10ms 下限根本够不着（**实测暴露，已修复**）

这是整个迁移里**唯一一次真的踩到 7.2 那条红线**，而且是被我们自己写的对比工具抓出来的，不是推演出来的。记在这里是因为它示范了「量化验收」的价值：如果只看单元测试，53 个测试全绿，这个问题永远不会暴露。

#### 怎么发现的

`compare-stream.ps1` 把 `interval=10`（契约允许的最小值）这一档跑成三协议全红：

| | Go | C#（修复前） |
|---|---|---|
| NDJSON 帧数（`duration=2`，期望 200） | 200 | **132** |
| NDJSON 帧间隔中位数 | 10.0 ms | **15.5 ms** |
| SSE 帧数 | 201 | **131** |
| WebSocket 帧数 | 200 | **132** |

C# 不是慢，是**被顶在一个 15.6ms 的地板上**：132 × 15.5ms ≈ 2046ms，时长跑满了，帧数少了三分之一。

#### 根因：Windows 系统时钟节拍

实测 `Task.Delay` 与 `Thread.Sleep` 的**延迟-实际曲线**（平均毫秒）：

| 请求 | 1 | 2 | 5 | 10 | 15 | 16 | 20 | 30 | 50 | 100 |
|---|---|---|---|---|---|---|---|---|---|---|
| `Task.Delay` | 15.31 | 15.61 | 15.18 | 15.65 | 18.44 | 27.75 | 31.03 | 31.70 | 62.27 | 109.18 |
| `Thread.Sleep` | 15.44 | — | 15.40 | 15.41 | — | 30.06 | — | — | 62.04 | — |

每一个数都是 `ceil(n / 15.625) × 15.625`。**15.625ms 就是 Windows 的默认时钟节拍**，两个原语都绕不过去。

一个反直觉的旁证：`NtQueryTimerResolution` 报告当前分辨率已经是 **1.0ms**（不是 15.625ms），但 `Thread.Sleep(10)` 依然要 15.4ms。所以「查询到的分辨率」不能当作「实际能达到的精度」——**这件事只能靠测，不能靠查**。

Go 为什么不受影响？Go runtime 在 Windows 上等的是 `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`（Win10 1803+），这个原语**不受系统节拍约束**，所以 Go 能稳定给出 10.0ms 中位数。

#### 修复：把高分辨率 waitable timer 接进 pacer

`dotnet/spike/TimerResolution/` 先验证「.NET 够不够得着这个原语」，再动生产代码。实测（目标 10ms，40 次采样）：

| 等待方式 | 中位数 | 平均 | 最小 | 最大 |
|---|---|---|---|---|
| `Task.Delay`（原 pacer） | 15.60 ms | 15.67 ms | 14.66 ms | 16.64 ms |
| `Thread.Sleep` | 15.59 ms | 15.64 ms | 14.68 ms | 16.67 ms |
| 高分辨率定时器（阻塞） | 10.23 ms | 10.27 ms | 10.09 ms | 10.61 ms |
| **高分辨率定时器（async）** | **10.32 ms** | 10.37 ms | 10.11 ms | 10.74 ms |

针对**绝对 deadline**的误差（这才是客户端看得见的量）：

| 等待方式 | 中位数 | p95 | 最差 |
|---|---|---|---|
| `Task.Delay` | 6.86 ms | 13.42 ms | 13.88 ms |
| 高分辨率定时器 | **0.42 ms** | **0.68 ms** | **0.70 ms** |

**async 形态必须单独测**：它把完成回调丢给线程池的 wait 线程，而「再排一次队」正是最可能把节拍重新引回来的地方。实测没有——所以生产代码用的是 async 形态，不必为每条流占一个专用线程。

设计上分两处，`Nsst.Core` 保持零平台调用：

- `Nsst.Core/Streaming/IDelayStrategy.cs` —— 只有接口和可移植实现 `TaskDelayStrategy`（就是 `Task.Delay`）
- `Nsst.Server/Streaming/HighResolutionDelayStrategy.cs` —— P/Invoke 实现，仅 Windows 生效，创建失败时**降级**回 `Task.Delay` 而不是让流失败

有一条安全约束写在代码里：**只要 `TimeProvider` 不是 `TimeProvider.System`（即测试用的 `FakeTimeProvider`），必须走可移植实现**。内核定时器看不见假时钟，用错了会把确定性测试变成真等——甚至挂死。

#### 修复后的实测结果

| 用例 | 修复前 Go / C# | 修复后 Go / C# |
|---|---|---|
| NDJSON 100ms 中位数 | 99.9 / 95.8 ms | **99.9 / 100.0 ms** |
| NDJSON 100ms 最大间隔 | 100.5 / 110.9 ms | **100.5 / 100.8 ms** |
| NDJSON 10ms 帧数 | 200 / **132** | 200 / **200** |
| NDJSON 10ms 中位数 | 10.0 / 15.5 ms | **10.0 / 10.0 ms** |

**收益不止于 10ms 那一档**：所有间隔下的中位数偏差从 ~4.5ms 收到 0.1ms 以内，最大抖动的差距也基本消失。`compare-stream.ps1` 从 17/20 变成 **20/20**。启动日志会明确打印用的是哪一档 pacer：

```
timer pacer=high-resolution (accurate at the 10ms floor) min_interval=10ms
```

**遗留偏差（诚实记录）**：非 Windows 平台、或 1803 以前的 Windows，会退回可移植实现，短间隔精度对应变差。这不是「差不多」，是明确的已知退化——降级时会打日志说明。Go 在所有平台都走高分辨率原语，这一点上 Go 更稳。

### 2.8 🔴 `ServeMux` 的路由语义与 ASP.NET 路由有 4 处可观测差异（**已逐条修复**）

新增 `dotnet/parity/compare-surface.ps1`（114 条用例：全部路由 × 全部方法、路径规范化、代理头、CORS、WebSocket 握手）逐条对拍后发现一个前提性结论：**响应体逐字节相同，并不等于契约相同。** 以下差异全部是「同一个 URL、同一个方法、两个框架给出不同决策」：

| # | 请求 | Go | 修复前 C# | 根因 |
|---|---|---|---|---|
| 1 | `HEAD` 打在 5 条 REST 路由上 | **200** | 405 | ServeMux 的 `GET <pattern>` **同时匹配 HEAD**；`MapGet` 不匹配 |
| 2 | `GET /api/health/`（尾部斜杠） | **405** + `Allow: GET, HEAD, OPTIONS` | 200 | Go 保留尾部斜杠，视作**不同 pattern**；ASP.NET 路由忽略它 |
| 3 | `GET /api/stream/http/` | **405** | **200，并且真的开了一条流**（93 KB） | 同上。这条最严重：一个拼错的 URL 会占满一整个流槽位 |
| 4 | 任何非规范路径（`//api/health`、`/api//health`、`/api/./health`、`/api/health/../health`） | **307 重定向**到规范路径 | 200 直接服务 | ServeMux **从不服务**非规范路径，一律 307 + `Location`；ASP.NET 不折叠重复斜杠 |

#### 第 4 条差点被漏掉，值得单独说

第一版 `compare-surface.ps1` 用的是 .NET `HttpClient`，它**默认自动跟随重定向**。于是 Go 的 `307 → /api/health → 200` 在结果里表现为「200」，而 C# 的「200 直接服务」也是「200」，两边看起来只差 body。我一度据此把 C# 改成「清洗路径后直接服务」，以为已经对齐——**实际是把一个 307 改成了 200，差异反而被固化。**

是换成 `curl --path-as-is`（不跟随、也不做本地路径折叠）之后才看清 Go 真正返回的是：

```
HTTP/1.1 307 Temporary Redirect
Content-Type: text/html; charset=utf-8
Location: /api/health
Content-Length: 47

<a href="/api/health">Temporary Redirect</a>.
```

细节都是契约：**查询串跟着 `Location` 走**；**只有 GET 有 body**（HEAD 有 `Content-Type` 但无 `Content-Length`，其他方法两者都没有，且 `Content-Length: 0`）；body 以**两个换行**结尾（Go 的 `Redirect` 写的消息自身带一个，`fmt.Fprintln` 再补一个）。

要复刻它还得拿到**原始请求行**：Kestrel 在应用看到请求之前就把 `.` 和 `..` 段解析掉了（`/api/./health` 到手时已经是 `/api/health`，要重定向的依据已经消失），但重复斜杠它又不动。只有 `IHttpRequestFeature.RawTarget` 保留了原始拼写。而且必须**用未解码的形式比较**——Go 是按 `r.URL.EscapedPath()` 匹配的，所以 `/%2e%2e/etc` 是一个字面段名而不是穿越，会落到 SPA；在这里解码反而会凭空造出一个 Go 不会发的重定向。

> 教训与 §7.1 下面那条同源：**先怀疑观测手段，再怀疑被测对象。** 这一条如果止步于「两个 200 差不多」，就会把一个真实差异当成已修复。

**已知差异（诚实记录）**：`/%2e%2e%2fetc%2fpasswd` 这类把斜杠也编码进去的路径，Go 走解码后的路径、落到 SPA 返回 `index.html`（200），C# 不把 `%2f` 解码成路径分隔符，于是把它当成一个带扩展名的文件名，返回 404。**这里刻意不对齐**：对一条形似目录穿越的请求回 404 不比回首页更差，而让 `%2f` 参与路径分段正是各类服务器拒绝解码它的原因。功能上无影响，记录在案。

同源的两条：

- **`GET /api/stream/ws` 不带升级头**：Go 回 **426 Upgrade Required** + 明文说明；修复前 C# 回**空的 200**（Kestrel 的 `AcceptWebSocketAsync` 抛异常后被吞掉）。`coder/websocket` 对畸形握手另有 4 种文案与状态码（`Upgrade` 缺失 → 426；版本非 13 → 400 + `Sec-Websocket-Version: 13`；缺 key → 400；key 非 16 字节 → 400），**全部以换行结尾**——Go 的 `http.Error` 会补 `\n`，而它是 `Content-Length` 的一部分。检查顺序也是契约：`Connection` → `Upgrade` → 版本 → key → **最后才轮 Origin**。
- **Origin 判定有两种错误**：解析不出 host 的（`null`、`not a url`）回 `is not a valid URL with a host`，能解析但 host 不匹配才回 `is not authorized`；后者引用的是**解析出的 host**（`https://evil.example` → `"evil.example"`）而非原始头，且协议部分不参与判定（同 host 的 `http://` 与 `https://` 都放行）。

**两个非显然结论（修复的前提）**：

1. `WebApplication` 会自动把 `UseRouting` 插到**管道最前面**。因此任何在路由之前改写 `Request.Path` 的中间件都必须显式 `app.UseRouting()` 把路由钉在自己后面，否则改写发生在路由决策**之后**，静默失效。第 3、4 项的修复依赖这一条。（第 4 项最终改成重定向、不再改写路径，但如果没有先发现这一点，会先被「改了没生效」卡住。）
2. 还原尾部斜杠语义**不能靠路由**，只能在中间件里判断并直接转交 `UnknownAsync`：ASP.NET 路由从设计上就不区分 `/a` 与 `/a/`，没有路由约束能表达「必须没有尾部斜杠」。

顺带修掉两处读 Go 源码时发现的偏差：CORS 的预检判定 Go 看的是 `Access-Control-Request-Method` **值非空**而非头存在（否则一个空的预检头会吞掉本该发生的 405）；`Recoverer` 在 Go 里写了 500 之后**吞掉** panic，C# 原先 `throw` 会把异常交给 Kestrel，此时响应已开始，连接被中止、客户端可能丢掉已经发出的 body。

> 方法论记录：`compare-stream.ps1` 曾报 `ws 10ms floor` 失败，说 C# 关闭 WebSocket 没走完握手。查下来是**工具自己的 bug**——客户端收到 Close 帧后直接 `Dispose()` 而没回 ack。用工具判定别人不合格之前，先确认工具自己合格，否则会为一个不存在的问题去改服务端。

---

## 3. 阶段划分

### Phase 0 —— 决策与 Spike（1～2 天）

在写任何生产代码之前，把三个未知数消掉。

| Spike | 内容 | 通过标准 |
|---|---|---|
| **A. 逐帧写超时** | 最小 Kestrel 应用，客户端建立连接后**停止读取** | 连接在 `WRITE_TIMEOUT_MS` 内被中止；`CancelTimeout()` 后的 pacing 等待（interval > WriteTimeout）不会被误杀 |
| **B. 多架构 + 镜像体积** | `dotnet publish -r linux-musl-x64 / -r linux-musl-arm64`（x64 宿主），构建 `aspnet:10.0-alpine` 镜像 | 两个 RID 都能在 x64 上产出；实测镜像体积，团队确认可接受 |
| **C. 字节级帧编码** | 复刻 `Encoder.Append`，与 Go 输出做 golden-file 比对 | 覆盖 `payload_size ∈ {0,1,79,80,81,1024}` × 多字节正文 × 跨换行边界，**全部逐字节相同** |

> Spike B 需要 Docker daemon（本机当前未运行）。
> Spike 产物可以放在临时目录，也可以留在 `dotnet/spikes/` 里当作回归证据。
>
> 🚩 **切换后回填的现状**：**Spike B 至今没有执行过** —— Docker 守护进程始终未运行，所以镜像体积与多架构产物仍未被实测（见 §9 第 10 项）。Spike A 的取舍已经落进实现（见 §8.3）；Spike C 要求的字节级保护由**提交进仓库的 golden 夹具**承担（`dotnet/tests/Nsst.Core.Tests/golden/frames.txt`、`params.txt`，见 §7.1）。

**决策门：** 🚩 **已作废** —— 原文是「三个 Spike 全部通过才进入 Phase 1；A 不通过就要重新评估方案（见 §2.2 的方案 B/C）」。Phase 1～7 已经走完，Go 树也已删除（见 §0.0.1），这道门不再有决策作用；真正残留的只有 Spike B。

---

### Phase 1 —— 解决方案骨架与配置（2～3 天）

目录结构（~~Go 树保持**原封不动**，两个实现并存，便于对比~~ 🚩 **已作废**：当时确实是这么摆的，但 Go 树现在已整体删除，见 §0.0.1）：

```
dotnet/
├── Nsst.sln
├── src/
│   ├── Nsst.Core/          # 纯逻辑：limits、params、pacer、manager、metrics、protocol
│   └── Nsst.Server/        # ASP.NET Core 宿主：路由、中间件、三个流式处理器
└── tests/
    ├── Nsst.Core.Tests/    # 移植 internal/stream + pkg/protocol 的测试
    ├── Nsst.Server.Tests/  # 移植 internal/api + 三个 handler + config 的测试
    └── Nsst.Parity/        # golden-file 与 Go/C# 并排对比
```

> 实际落地的形态与上图有两处出入：解决方案文件是 **`Nsst.slnx`**；**`Nsst.Parity` 从未建成**，golden 夹具直接放在 `Nsst.Core.Tests/golden/` 下。

- 目标框架 `net10.0`（本机已装 10.0.401 SDK / 10.0.12 运行时，LTS）。
- 移植 `internal/config` → `ServerOptions`：
  - 环境变量名、默认值、范围**完全一致**
  - 保留 `LoadFrom(Func<string,string>)` 这个可注入形状 → C# 里写成 `LoadFrom(Func<string, string?>)`，测试才好写
  - 非法值 / 超范围 / `MAX_INTERVAL < MIN_INTERVAL` → **启动失败并给出同样的错误文案**
  - 默认值夹取逻辑（`clamp`）照搬
- 移植 `metrics.Counters`。已实测 .NET 10 有 `Interlocked.Increment(ref ulong)` 和 `Interlocked.Add(ref ulong, ulong)`，**可以直接用 `ulong`**，不必退化成 `long`。
- ~~CI 增加 `dotnet` job，Go job **暂时保留**。~~ 🚩 **已作废**：Go job 随 Go 树一起删除，CI 正在单独改写（见 §9 第 10 项）。

**退出门槛：** `loadFrom` 与 counters 的移植测试全绿，环境变量表逐项对齐 README。

---

### Phase 2 —— 帧协议与流引擎（3～4 天，纯逻辑、测试价值最高）

先移测试，再移实现。这些测试就是规格：

- `Nsst.Core/Limits.cs` ← `limits.go`：`ParseParams` **错误文案逐字符一致**（README 里把错误 JSON 当契约写了）
- `Nsst.Core/Pacer.cs` ← `pacer.go`
  - 绝对网格 `next`
  - `next - now < -interval` 时重对齐
  - **绝不补发**
- `Nsst.Core/StreamRunner.cs` ← `stream.go`
  - 首帧立刻发（t=0）
  - `EndReason`：`completed` / `client_closed` / `write_error` / `server_shutdown`
  - 取消原因分类 —— Go 的 `context.CancelCause` 在 C# 里没有对应物，需要自建承载原因的类型（例如一个带 `EndReason` 的异常或包装结构），**不要用 `OperationCanceledException` 硬猜**
- `Nsst.Core/Protocol.cs` ← `protocol.go`
  - 帧编码：`ArrayBufferWriter<byte>` + 手动写入，或 `Utf8JsonWriter`（注意 §2.4 的编码器）
  - 编码器缓存（上限 32，按 `payload_size` 键）
- `Nsst.Core/PayloadDocument.cs` ← `payload.go`
  - 启动时读一次并替换
  - 文件不存在 → **创建并以 embed 正文播种**（含创建父目录）
  - 是目录 → 报错并点明"Docker bind mount 源文件缺失会创建目录"
  - 非 UTF-8 / 空 / 全空白 → 启动失败
  - 已实测 `Ascii.IsValid(ReadOnlySpan<byte>)` 与 `Rune.DecodeFromUtf8` 均可用 → ASCII 快路径与 rune 边界对齐都能 1:1 移植
  - 正文必须以 `byte[]`（UTF-8）保存并按字节切分，**不要用 `string`**（UTF-16 下标与字节数不等价）

**新增：golden-file 测试。** 用 Go 侧生成一个矩阵的期望输出（`payload_size` × 正文 × offset，含跨换行、多字节、绕回）存为测试数据，C# 断言逐字节相同。

**退出门槛：** 移植 44 个测试（protocol 19 + stream 25）全绿 + golden-file 全绿。

---

### Phase 3 —— HTTP 接口层（3～4 天）

- Minimal API 映射**同样的路由**，5 个 REST 端点。
- **CORS 必须手写，不要用 `AddCors` / `UseCors`。** Go 实现的语义是自己的：
  - 未配置来源 → **完全不发 CORS 头**
  - `*` → `Access-Control-Allow-Origin: *`
  - 白名单 → 回显来源 + `Vary: Origin`
  - 带 `Access-Control-Request-Method` 的 `OPTIONS` → **即使来源不允许也返回 204**（只是不带 CORS 头）
  - 中间件覆盖**所有**路径，包括 `/api/stream/*`
- 错误信封与 404/405：
  - ASP.NET 路由在"路径匹配但方法不对"时会**自动**返回 405 并带 `Allow` 头 —— 但响应体是空的，必须拦截并改写成 `{error,message,param}` 信封
  - 未知 `/api/*` → `not_found`，且**绝不允许**掉进 SPA 回退
- 异常处理中间件替代 `httpx.Recoverer`，写出同一个信封。
- Kestrel 显式配置（实测默认值 → 目标值）：

| 项 | .NET 10 默认 | Go 现状 | 目标 |
|---|---|---|---|
| `RequestHeadersTimeout` | `00:00:30` | `ReadHeaderTimeout: 10s` | `10s` |
| `KeepAliveTimeout` | `00:02:10` | `IdleTimeout: 120s` | `120s` |
| `MaxRequestHeadersTotalSize` | `32768` | `MaxHeaderBytes: 32<<10` | 已一致，无需改 |
| `MinResponseDataRate` | `240 B/s, grace 5s` | 无对应 | **`null`**（见 §2.1） |
| `MaxRequestBodySize` | `30000000` | 无请求体 | 设为 `0` 或极小 |
| 响应写超时 | 无全局项 | 不设（逐帧设） | 不设全局项，走 §2.2 |

- `/api/client`：已实测 `ITlsHandshakeFeature` 存在（`SslProtocols Protocol`、`HostName`），可以填 `tls` / `tls_version`。
  **待验证：** `HttpRequest.Protocol` 在 ASP.NET Core 返回 `"HTTP/2"`，而 Go 返回 `"HTTP/2.0"` —— 这是 README 记录在案的字段，需要归一化，否则前端显示和 CSV 导出会变。

**退出门槛：** 移植 17 个 api 测试 + 4 个 httpx 测试 + 5 个 config 测试全绿。

---

### Phase 4 —— 三个流式端点（4～5 天，风险最高）

**NDJSON**（`httpstream`）
- `Content-Type: application/x-ndjson`、`Cache-Control: no-store`、`X-Accel-Buffering: no`
- 先 `FlushAsync()` 一次再进主循环
- 每帧：写帧 + 写 `\n` + `FlushAsync()`（用 `IHttpResponseBodyFeature.Writer`，必要时先 `DisableBuffering()`）

**SSE**
- 响应头：`text/event-stream`、`Cache-Control: no-cache`、`Connection: keep-alive`、`X-Accel-Buffering: no`
- 开场写 `: stream open\n\n` 后立即 flush
- 帧格式 `id: N\nevent: data\ndata: {...}\n\n`，`last` 超过 15 秒先写 `: keep-alive\n\n`
- 注意：ASP.NET Core **不会**自动加 `Connection: keep-alive`（HTTP/2 下更是禁止该头），必须按 HTTP/1.1 条件显式设置

**WebSocket**（最高风险）
- 租约在**升级之前**申请 —— 这一点在 ASP.NET Core 里反而更自然：先写 503 JSON 信封，再决定是否 `AcceptWebSocketAsync`
- 关闭压缩、不协商子协议
- 断开感知：必须跑一个读循环（`ReceiveAsync` 直到 `Close`），否则感知不到对端关闭
- 关闭握手：`CloseOutputAsync(1000/1001/1011, reason)` + **2 秒宽限**，超时 `Abort()`
- **Origin 校验必须手写**：ASP.NET Core 默认**不**校验 WebSocket 的 `Origin`，而 Go 侧有 `OriginPatterns` / `InsecureSkipVerify` 的对应行为
- 用 `CancellationTokenSource.CreateLinkedTokenSource` 复刻 `Manager.Acquire` 里 `context.AfterFunc(m.base, ...)` 的父子取消联动
- 配额用 `SemaphoreSlim` 或基于 `Channel` 的计数信号量，保持**不阻塞、立即失败返回 503** 的语义
- 排空循环保持 20ms 轮询以复刻 `Manager.Drain` 的语义

**退出门槛：** 移植 7（httpstream）+ 5（sse）+ 7（websocket）= 19 个测试全绿，含"客户端断开后立即释放配额"与"WebSocket 会话不泄漏"。

---

### Phase 5 —— 内嵌前端与生命周期（2 天）

- `web/` **源码不动**，只改 `web/vite.config.ts` 的 `outDir` 指向 C# 侧的资源目录。
- 用 `EmbeddedResource` + `ManifestEmbeddedFileProvider`（`Microsoft.Extensions.FileProviders.Embedded`），保住"单个可执行文件"这个性质；或者放 `wwwroot` 用 `UseStaticFiles`（但会失去内嵌）。
- SPA 回退逻辑照搬：
  - `/api/*` 永不回退
  - 无扩展名的未知路径 → `index.html`
  - 有扩展名但文件不存在 → 404
  - `assets/*` → `Cache-Control: public, max-age=31536000, immutable`
  - `*.html` → `Cache-Control: no-cache`
- 优雅关闭：
  - `IHostApplicationLifetime.ApplicationStopping` → `StartDraining()`
  - 一个 `HostedService` 在 Kestrel 停止**之前**执行排空（注册顺序：后注册的 hosted service 先停止，需实测确认顺序）
  - 排空 → 超时则强制取消 → 分配 5 秒收尾
  - **按 §2.3 显式设置 `HostOptions.ShutdownTimeout`，并对齐 `stop_grace_period`**
  - 验证 `SIGTERM` → 退出码 `0`（不是 137）

---

### Phase 6 —— 打包与 CI/CD（2～3 天）

> **切换后回填的现状：**
> - **`Dockerfile` 已经在仓库里**（三段式，与本节描述基本一致），**`Makefile` 也已经重写**。
> - **CI 正在由另一处改动单独改写**，本节关于 `ci.yml` / `release.yml` 的内容属于当时的计划，不代表现在 workflow 里的内容。
> - ⚠️ **镜像一次都没构建过**：Docker 守护进程未运行，所以本节的体积判断、非 root、健康检查、多架构产物**全部未经验证**（见 §9 第 10 项）。

**Dockerfile**（三段，语义对齐现有设计）

```
web     (node:22-alpine,  ${BUILDPLATFORM})  → 前端产物
sdk     (dotnet/sdk:10.0-alpine, ${BUILDPLATFORM}) → dotnet publish -r linux-musl-${TARGETARCH}
runtime (aspnet:10.0-alpine)                → 非 root uid 10001 + HEALTHCHECK + LABEL
```

- 保留：非 root uid 10001、`EXPOSE 8080`、`STOPSIGNAL SIGTERM`、Oci LABEL、busybox `wget` 健康检查（`aspnet:10.0-alpine` 是 Alpine，有 wget）
- 保留：`web` 阶段固定在 `${BUILDPLATFORM}`，前端只构建一次
- **变化：** 非 AOT 的 `dotnet publish -r <RID>` 仍是跨 RID 的 IL 发布，可以在 x64 上原生产出 arm64，**不触发 QEMU** —— 这一点还能守住
- **但 NativeAOT 会彻底失去这个性质**（见 §6 补充）

**CI（`ci.yml`）**
- 新 `dotnet` job：`dotnet format --verify-no-changes`、`dotnet build -warnaserror`、`dotnet test`
- 容器冒烟测试**断言不变**：NDJSON 4 帧、SSE 4 个 `data:`、首页可访问、bundle 资源可取
  - 注意 SSE 那一条会直接踩 §2.1 的坑，这是很好的早期警报
- ~~Go job 在切换前保留~~ 🚩 **已作废**：Go job 随 Go 树一起删除，CI 正在单独改写。

**Release（`release.yml`）**
- **这是一处实打实的降级，必须显式决策。** Go 用 `GOOS/GOARCH` 免费产出 5 个平台的自包含二进制；.NET 要么 per-RID 自包含（每个约几十 MB，5 个 RID 更慢），要么 `PublishSingleFile`，要么 NativeAOT（需要对应平台的原生 runner 或 QEMU）。三个选项都要重新设计矩阵。

**Makefile**
- ~~目标名保持不变（`build`/`test`/`lint`/`smoke`/`docker`），只换实现，保留团队肌肉记忆。~~ **实际结果有出入**：Makefile 已重写，目标集比原来大得多，现在是 `help deps web build run test test-cover fmt fmt-check lint smoke smoke-full docker docker-run docker-smoke clean all`。

---

### Phase 7 —— 并行验证与切换（3～4 天）

> 🚩 **本节一半作废、一半已完成。** 「并行验证」那一半随 `dotnet/parity/` 一起消失了（见 §0.0）；「切换」那一半已经执行完毕（见 §0.0.1）。下面的清单保留作历史记录，**不要再照着执行**。

**并排运行：** Go 在 `:8080`，C# 在 `:8081`。

**契约对比（`tests/Nsst.Parity`）：** 同一批请求打到两个实现，逐项 diff：

- 状态码、`Allow`、`Retry-After`、全部响应头
- 错误信封 JSON
- **帧 JSON 的原始字节**（不是反序列化后比较）
- SSE 的完整字节流
- WebSocket 握手响应头与关闭码/原因
- `/api/metrics`、`/api/config`、`/api/info` 的字段名与结构
- 环境变量非法值的启动失败文案

**pacing 对比（真正的验收，见 §7.2）：** 这个工具本身就是测量帧间隔稳定性的，所以**服务端自己的 pacing 质量必须可量化对比**：

- 场景：3 协议并发 × `interval=10ms`（`MIN_INTERVAL`）× 多种 `payload_size`，各跑 60 秒
- 指标：帧间隔的 **p50 / p99 / p99.9 / 最大值 / 超阈值次数**
- ~~阈值：C# 的 p99 抖动不劣于 Go 超过一个明确数字（**这个数字要在 Phase 0 就定下来**，否则最后会变成"感觉还行"）~~ 🚩 **已作废**：Go 与对比工具都已删除，**没有对照物可以重跑**，阈值必须改写成**绝对阈值**（见 §7.2 与 §9 第 6 项）。这个数字至今仍未定。
- 若劣化明显：热路径零分配（复用缓冲、`ArrayPool<byte>`、避免每帧装箱/字符串）、`ServerGarbageCollection` 调优、必要时用 `GC.TryStartNoGCRegion` 思路或改用 `PipeWriter` 直写

**切换（已完成，且与当时的计划有出入）：**
- 更新 README 中所有"单个 Go 二进制"、"镜像约 21 MB"、项目结构、测试数量（89 个）、构建/测试命令 —— README 的改动**由另一处改动单独负责**，本文不记录其状态。
- ~~Go 代码移到分支或直接删除（**决策点，见 §9**）~~ → **实际是直接删除**：`cmd/`、`internal/`、`pkg/`、`go.mod`、`go.sum` 全部移除（见 §0.0.1）。
- ~~保留已发布的 Go 镜像 tag，回滚 = `docker pull` + 换 tag~~ → 🚩 **未执行，也不再成立**：Go 树已删除，而 .NET 镜像从未构建过，因此**现在没有任何回滚路径**。

---

## 4. 工作量估算

生产代码 ≈ 2,600 行，测试 ≈ 4,000 行需要移植。

| 路线 | 工期 |
|---|---|
| 1 人、Go 与 C# 都熟 | **4～6 周** |
| 2 人并行（一人 Phase 2–3，一人 Phase 4–6） | **3～4 周** |

其中 Phase 0（Spike）与 Phase 7（并行验证）**不可压缩**，合计约占 1 周 —— 这两段恰恰是最容易在排期里被砍掉、然后导致返工的部分。

---

## 5. 文件映射速查

> 说明：左列的 Go 文件**已全部删除**（见 §0.0.1）。本表保留下来是因为它仍然回答「C# 里哪一段是原来哪一段的对应物」，对读老记录和 review 迁移差异有用。

| Go | C# |
|---|---|
| `cmd/server/main.go` | `src/Nsst.Server/Program.cs` + `Shutdown/DrainService.cs` |
| `internal/config/config.go` | `src/Nsst.Core/Configuration/ServerOptions.cs` |
| `internal/stream/limits.go` | `src/Nsst.Core/Streaming/Limits.cs`、`StreamParams.cs` |
| `internal/stream/pacer.go` | `src/Nsst.Core/Streaming/Pacer.cs` |
| `internal/stream/stream.go` | `src/Nsst.Core/Streaming/StreamRunner.cs`、`EndReason.cs` |
| `internal/stream/manager.go` | `src/Nsst.Core/Streaming/StreamManager.cs`、`StreamLease.cs` |
| `internal/stream/report.go` | `src/Nsst.Server/Logging/StreamLog.cs` |
| `internal/metrics/metrics.go` | `src/Nsst.Core/Metrics/Counters.cs` |
| `internal/httpx/httpx.go` | `src/Nsst.Server/Http/ErrorEnvelope.cs`、`CorsMiddleware.cs`、`ExceptionMiddleware.cs` |
| `internal/httpx/clientip.go` | `src/Nsst.Server/Http/ClientAddress.cs` |
| `internal/api/api.go` + `client.go` | `src/Nsst.Server/Endpoints/ApiEndpoints.cs` |
| `internal/httpstream/handler.go` | `src/Nsst.Server/Streaming/NdjsonEndpoint.cs` |
| `internal/sse/handler.go` | `src/Nsst.Server/Streaming/SseEndpoint.cs` |
| `internal/websocket/handler.go` | `src/Nsst.Server/Streaming/WebSocketEndpoint.cs` |
| `internal/webui/webui.go` | `src/Nsst.Server/WebUi/EmbeddedSpa.cs` |
| `pkg/protocol/protocol.go` | `src/Nsst.Core/Protocol/FrameEncoder.cs`、`Frame.cs` |
| `pkg/protocol/payload.go` | `src/Nsst.Core/Protocol/PayloadDocument.cs` |

---

## 6. 风险清单

| # | 风险 | 等级 | 缓解 |
|---|---|---|---|
| 1 | 逐帧写超时无 1:1 对应（§2.2） | 中 ↓ | **可补，见 §8.3**：`CancelAfter` + 异常过滤器精确分类，`IConnectionTimeoutFeature` 兜底拆连接 |
| 2 | Kestrel 默认 240 B/s 掐死稀疏流（§2.1） | **高** | 显式 `MinResponseDataRate = null`；CI 冒烟测试会早期暴露 |
| 3 | JSON 转义集不一致（§2.4） | 中 | 显式指定编码器或手写转义；golden-file 覆盖含 `&` 的自定义正文 |
| 4 | 关闭超时不匹配导致退出码 137（§2.3） | 中 | 三处超时排成一致关系并写进文档 |
| 5 | 取消原因模型差异 → 指标口径漂移 | 中 | 自建承载 `EndReason` 的取消类型，不用 `OperationCanceledException` 猜 |
| 6 | `HttpRequest.Protocol` 是 `HTTP/2` 而非 `HTTP/2.0` | 低 | 归一化；补一条契约测试 |
| 7 | WebSocket 无内建 Origin 校验 | 中 | 手写校验；补跨域用例 |
| 8 | GC 停顿影响 pacing 质量 | **高** | 热路径零分配；Phase 7 量化对比（§7.2） |
| 9 | 镜像体积增长 2～3 倍 | 中 | Phase 0 Spike B 实测；接受或评估 NativeAOT |
| 10 | Release 的 5 平台二进制矩阵变复杂 | 中 | 显式决策：per-RID 自包含 / SingleFile / NativeAOT / 放弃 |
| 11 | 失去 `-race`（**唯一补不回来的**，§8.2） | 中 | 共享状态仅 3 处、可枚举；Roslyn 分析器 + 构造上禁止共享可变状态 + 重点 review |
| 12 | 失去 goroutine 泄漏检测 | 低 ↓ | **能补且更好**（§8.3）：复用已有 `ActiveStreams` 计数器做语义层断言 |
| 13 | NativeAOT 摧毁"无 QEMU 多架构"（见下方补充） | 中 | 本阶段不使用 NativeAOT |
| 14 | **等待原语被量化到 15.6ms 节拍，契约允许的 10ms 下限不可达**（§2.7） | **高 → 已修复** | 接 `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`（`dotnet/src/Nsst.Server/Streaming/HighResolutionDelayStrategy.cs`）；`compare-stream.ps1` 实测 20/20；非 Windows / 旧版 Windows 降级回 `Task.Delay` 并在启动日志声明 |

> ⚠️ **两处回填说明：**
> - **第 9 项挂在未执行的 Spike B 上**：Docker 守护进程始终未运行，镜像体积至今是未知数，「接受或评估 NativeAOT」也还没有依据（见 §9 第 10 项）。
> - **第 14 项的证据来源已消失**：`compare-stream.ps1` 已随 `dotnet/parity/` 删除，那条「20/20」不能再重跑；高分辨率 pacer 本身仍在代码里，`dotnet/spike/TimerResolution/` 保留着当时的实测工具。

### 补充：为什么本阶段不用 NativeAOT

- NativeAOT **不支持跨架构交叉编译**，arm64 镜像必须在 arm64 上构建（原生 runner 或 QEMU），直接失去现在"构建一次、原生交叉编译"的性质。
- 需要 `System.Text.Json` 源生成、反射型 DI 与序列化全部 AOT 安全。
- 与 §2.2 的 `IConnectionTimeoutFeature`（Kestrel 内部特性）在 AOT 下的可用性需要额外验证。
- 结论：**先用普通 ASP.NET Core 把功能和契约做对**，NativeAOT 作为后续独立优化，并且要有单独的体积收益预算。

---

## 7. 验收标准

### 7.1 契约等价（必须 100% 通过）

- Phase 7 的全部契约对比项逐字节相同
- 移植后的测试套件全绿（目标 ≥ 89 个测试函数）
- README 里所有"实测结果"表格在 C# 实现上重新跑一遍，逐项复核

#### 当前状态（🚩 已于 §0.0 重新定义：不再以「和 Go 逐字节一致」为验收项）

| 验收项 | 现状 | 证据 |
|---|---|---|
| 帧格式（前端解析的契约） | **975/975** golden 用例 | `dotnet/tests/Nsst.Core.Tests` |
| 参数校验 | **50/50** golden 用例 | 同上 |
| 测试套件 | **134 个全绿**（Core 53 + Server 81） | `dotnet test dotnet/Nsst.slnx -c Release` |
| 前端 REST 契约（字段名/类型/单位） | ✅ 5 个端点逐一核对 | `web/src/api.ts` + `types.ts`，见 §0.0 |
| 正确性行为：HEAD / 尾部斜杠 405 / WS 426 | ✅ 实测 | 见下方 |
| 优雅关闭端到端 | ✅ 关机时不截断在飞流（10/10 帧） | `GracefulShutdownTests` |
| ~~与 Go 逐字节对比~~ | **已作废**，对比工具已删除 | —— |
| **pacing 验收（§7.2）** | ❌ **未做**（判据还必须改写成绝对阈值） | —— |

**曾经记录过的两条 Go 侧差异**（工具已删，保留结论）：Go 的 `time.ParseDuration` 会在 int64 纳秒上溢出回绕，把「太大」误报成「太小」——C# 不复刻这个瑕疵；开发机上的 Go 走 Windows 注册表把 `.js` 报成 `application/javascript`，C# 用框架自带的表，报 `text/javascript`。

**保留的三条正确性行为**（实测通过，与 Go 无关，丢掉就是功能退化）：

- `HEAD` 打在 5 条读接口上返回 **200**；
- `/api/stream/http/`（尾部斜杠）返回 **405 + `Allow`**，**不打开流**——实测 5 个尾部斜杠请求合计 166ms，真开了流会是秒级；
- 不带升级头的 `GET /api/stream/ws` 返回 **426 + 明文原因**，版本不符返回 **400**，都不是空 200。

#### 尾部斜杠护栏应该放在管线的哪一层

这个位置不直观，而且放错会静默退化，所以把结论和推理都记下来。最终顺序：

```
Recoverer → UseRouting → VaryOrigin → UseCors → ApiPathMiddleware → UseWebSockets → 端点
```

两个约束把位置夹住了：

1. **必须在 `UseCors` 之下**。护栏自己写响应，写在 CORS 之前就一个 CORS 头都没有——跨域调用方（路径拼接 bug 多打一个斜杠）拿到的是浏览器的 **CORS 报错**，而不是那句「method GET is not allowed for /api/health」。**护栏存在的意义就是给出这个诊断，写在 CORS 之上等于把它藏起来。** 实测确认过：改之前 `/api/health/` 的 405 既无 `Access-Control-Allow-Origin` 也无 `Vary: Origin`，而 `/api/health` 和 `/api/nope` 都有。
2. **必须在 `UseWebSockets` 之上**。`UseWebSockets` 是**在中间件里完成握手的**，不是在端点里——所以它下面的护栏对 `/api/stream/ws/` 已经无法回答 405 了，只能看着 101 升级完成。这条是动手前专门去确认的，因为它决定了「能不能下移」这个前提成立与否。

那「必须在 `UseRouting` 之上」这个约束呢？**它不成立，这是个误解。** `UseRouting` **只做匹配**：它选中端点并挂到 `HttpContext` 上，端点本身要等到管线末端的终结中间件才执行。所以护栏放在 `UseRouting` 之后照样能拦住流——它替换的是响应，而端点还没跑。

顺带记一个同类陷阱：`UseCors` 会为预检请求直接短路返回 204，够不到护栏。这是**对的**——预检问的是「我能不能发这个请求」，不是「这个路径存不存在」。

验证方式：`RoutingTests` 断言 5 条路径的尾部斜杠都是 405 且 `StreamsStarted == 0`（状态码单独不足以证明什么都没跑），另外在真 Kestrel 上单独确认过 `/api/stream/ws/` 的真握手是 **405 而不是 101**——`TestServer` 的 WebSocket 实现和 Kestrel 不是同一套，所以这一条非实测不可。

#### 一个教训：对比工具本身也会撒谎

（这一节保留，因为教训与 Go 无关，是通用的。）

当时的对比工具曾报 `ws 10ms floor` 失败，说 C# 关闭 WebSocket 时没走完握手。看起来像服务端缺陷，实际是**工具自己的 bug**：客户端收到 Close 帧后直接 `Dispose()` 而没有回 ack。补上 ack 之后连跑 3 次都通过。

**用工具判定别人不合格之前，先确认工具自己合格。** 如果当时直接去改服务端，就会为一个不存在的问题引入真实的风险。

后来还有一次同源的：第一版 HTTP 面对比工具用 .NET `HttpClient`，它**默认跟随重定向**，于是 Go 的 `307 → 200` 被记成「200」，差异被抹平。换成 `curl --path-as-is` 才看见真身。<u>观测手段会替你决定你看见什么。</u>

### 7.2 pacing 不劣化（真正的验收）

**这个项目卖的就是帧间隔稳定性。** 如果服务端自己的 pacing 变差，迁移就是负收益 —— 哪怕所有测试都绿。

必须量化，且在 Phase 0 就定下阈值：

| 指标 | 采集方式 |
|---|---|
| 帧间隔 p50 / p99 / p99.9 | 三协议并发，`interval=10ms`，各 60 秒 |
| 最大帧间隔 | 同上 |
| 超阈值间隙次数 | 用工具自己的阈值公式 `max(3×interval, interval+250ms)` |
| 服务端 CPU / 内存 / GC 次数 | 同场景下采集 |

**判据示例（需团队确认具体数字）：** `interval=10ms` 下，C# 的 p99 帧间隔 ≤ Go 的 p99 + 5ms，且超阈值间隙次数不超过 Go 的 1.5 倍。

不达标就优化热路径，而不是放宽阈值。

#### 当前状态（提前命中过一次，已修复）

这条判据**在 Phase 7 之前就触发过一次**：`compare-stream.ps1` 在 `interval=10ms` 档上测出 C# 只交付 132/200 帧、中位数 15.5ms，根因是 .NET 等待原语被量化到 Windows 的 15.625ms 节拍（详见 §2.7）。修复后：

| 档位 | Go 中位数 / 最大 | C# 中位数 / 最大 | 结论 |
|---|---|---|---|
| 10 ms | 10.0 / 11.0 ms | 10.0 / 10.6 ms | 达标 |
| 100 ms | 99.9 / 100.5 ms | 100.0 / 100.8 ms | 达标 |
| 300 ms | 299.5 / 306.7 ms | 299.4 / 299.8 ms | 达标 |

`compare-stream.ps1` 现为 **20/20**（NDJSON / SSE / WebSocket 各档，逐字节比帧序列 + 对比 pacing 分布）。

**但 7.2 的完整判据仍未满足**，不要用上面这张表冒充：

- 目前的采集是**单协议、短时长**（1～2 秒），拿到的是中位数与最大值，**不是 p99 / p99.9**；
- 尚未做**三协议并发 60 秒**的场景；
- 尚未采集**服务端 CPU / 内存 / GC 次数**；
- 尚未跑 `duration` 长尾与慢客户端（`write_timeout` 生效）的交叉场景。

这些是 Phase 7 的工作，也是最终 go / no-go 的证据来源。

---

## 8. 能力损失与「能不能自己手搓」

前一节列的是**现象**，这一节回答**能不能补回来** —— 这才是决定项目能不能真的交付的东西。

### 8.1 逐项核对

| 丢失的能力 | Go 侧 | ASP.NET Core 侧 | 能否手搓 | 成本 |
|---|---|---|---|---|
| 逐帧写超时 | `ResponseController.SetWriteDeadline` | 无等价 API | ✅ **能，且能更精确** | 中 |
| 取消原因 | `context.CancelCause` / `context.Cause` | `CancellationToken` 不携带原因 | ✅ 能 | 低 |
| 父子取消联动 | `context.AfterFunc` | — | ✅ 能 | 低 |
| 逐帧 flush | `ResponseController.Flush` | `IHttpResponseBodyFeature.Writer.FlushAsync` | ✅ 直接等价 | 无 |
| 内嵌静态资源 | `go:embed` | `EmbeddedResource` + `ManifestEmbeddedFileProvider` | ✅ 直接等价 | 低 |
| **取消竞态检测** | `go test -race` | **无** | ❌ **不能** | — |
| 协程/会话泄漏检测 | `runtime.NumGoroutine()` 基线 | 无等价计数器 | ✅ 能，且更好 | 低 |
| 跨平台交叉编译 | `GOOS`/`GOARCH` 单条命令 | IL 发布可跨 RID；NativeAOT 不行 | ⚠️ 部分 | 中 |
| 21 MB 静态单文件镜像 | `CGO_ENABLED=0` | 自包含约 60～80 MB | ⚠️ 部分 | 高 |
| GC 对 pacing 的影响 | 低停顿 | 可调，但必须实测 | ⚠️ 可缓解 | 中 |

**结论先行：10 项里 5 项直接等价或纯赚，4 项能补但有代价，只有 1 项补不回来。**

### 8.2 唯一真正补不回来的：`-race`

.NET **没有** ThreadSanitizer 集成，`dotnet test` 也没有竞态检测模式。这是永久性损失，没有等价替换。

但要说清楚这个损失**到底有多大**。本项目需要竞态保护的共享可变状态是可枚举的：

| 位置 | 共享状态 | 保护方式 |
|---|---|---|
| `metrics.Counters` | 8 个计数器 | 纯原子操作 |
| `stream.Manager` | 信号量 + 3 个原子量 + base context | 原子 + channel |
| `protocol.encoderCache` | 一个 map | 互斥锁 |

**只有 3 处**，而且都能通过构造保证正确：只用原子操作、初始化后不可变、不共享可变集合。

所以这不是"我们失去了安全性"，而是"我们失去了对一个**小而可枚举**的表面的自动化验证器"。缓解手段（不能替代，但能覆盖大部分）：Roslyn 分析器（`Microsoft.VisualStudio.Threading.Analyzers` 能抓一批常见模式）、设计上禁止共享可变状态、压力测试重复跑、以及 code review 时对这 3 处重点过。

### 8.3 补得比 Go 更好的两项

**（1）逐帧写超时 —— 能补，而且能比 Go 更精确。**

§2.2 里我担心的是"连接被中止 → 会被误分类成客户端断开"。用两个机制叠加就能解决，而且这个分类比 Go 更干净：

```csharp
// 两个机制各自覆盖对方的短板：
//   CancelAfter                 → 让循环及时退出，并给出可分类的异常
//   IConnectionTimeoutFeature   → 确保 socket 真的被拆掉（不依赖上层是否响应取消）
using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
writeCts.CancelAfter(_limits.WriteTimeout);

var timeouts = context.Features.Get<IConnectionTimeoutFeature>();
timeouts?.SetTimeout(_limits.WriteTimeout);
try
{
    await body.Writer.WriteAsync(frame, writeCts.Token);
    await body.Writer.FlushAsync(writeCts.Token);
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    // 外层请求 token 没被取消 ⇒ 是我们自己的写超时，而不是对端断开
    reason = EndReason.WriteError;
    context.Abort();
    throw;
}
finally
{
    timeouts?.CancelTimeout();
}
```

关键就是 `when (!ct.IsCancellationRequested)` 这个异常过滤器 —— 它是 Go 里 `classify()` 的对应物，**能干净地区分"写超时"和"对端断开"**。

因此 §2.2 的风险等级从「高」下调到「中」：口径漂移是可以避免的，不再是被迫接受的妥协。（仍需 Spike 确认 `CancelAfter` 能可靠打断一个卡住的 Kestrel flush；即使不能，`IConnectionTimeoutFeature` 也保证了连接一定会被拆掉。）

**（2）会话泄漏检测 —— 能补，而且比 Go 的版本更有语义。**

Go 侧测的是 `runtime.NumGoroutine()` 相对基线的数量，这是**实现层**的观察。而本项目 `Counters.ActiveStreams` 已经存在，它测的是**语义层**的问题：配额到底有没有还回去。

所以 C# 版的等价测试反而更直接：跑 N 个会话 → 断言 `ActiveStreams` 回到 0、且租约全部释放。这比数协程更贴近我们真正关心的事。

### 8.4 会得到什么（以及为什么收益比想象中小）

真实收益：

- **团队主力语言的维护效率** —— 这是唯一强的、也是唯一真正的理由
- 调试与诊断工具链：Rider/VS 调试器、`dotnet-trace` / `dotnet-counters` / `dotnet-gcdump` / PerfView
- Roslyn 分析器在静态检查上明显强于 `gofmt` + `go vet`
- `WebApplicationFactory` 提供进程内集成测试，比 `httptest` 顺手
- 若将来要加认证 / 数据库 / OpenTelemetry / OpenAPI，ASP.NET Core 全是一等公民
- 可空引用类型、模式匹配、LINQ

**但要诚实：这个项目没有数据库、没有认证、没有外部依赖、就是一个自包含的测量工具。** `go.mod` 里只有**一个**依赖（`coder/websocket`）。上面那些生态收益，大部分在本项目里**用不上**。

也就是说：收益几乎全部集中在"**谁来长期维护它**"这一条上。如果团队确实会长期用 C# 维护，这条收益是实打实的；如果只是技术栈偏好，这条收益是要打折扣的。

### 8.5 我的判断

**技术上可以做，没有阻塞项。** 10 项能力里 9 项能补回来或直接等价，其中 2 项补得比 Go 更好；唯一补不回来的 `-race`，暴露面只有 3 处可枚举的共享状态。

**代价是明确的、不可消除的三条：**

1. 镜像从 21 MB 涨到 2～3 倍
2. 放弃 Go 免费提供的 5 平台小体积自包含二进制
3. pacing 质量从"默认就没问题"变成"必须主动管理并持续验证"

**所以我的结论是：可以做，但要挂一个明确的止损条件（kill criterion）。**

> **唯一的否决条件：** Phase 7 的 pacing 实测不达标，且在热路径零分配 + ServerGC 调优之后**仍然**不达标。
>
> 到那个时候应该**停手、保留 Go**，而不是放宽阈值 —— 因为一个"测量工具自身抖动很大"的产品，等于没有产品。

---

## 9. 决策状态

### 已确认

| # | 决策 | 结论 |
|---|---|---|
| 1 | **迁移方式** | ✅ **切换已完成**：`dotnet/` 是仓库里唯一的实现，Go 树（`cmd/`、`internal/`、`pkg/`、`go.mod`、`go.sum`）已删除。回滚手段变成 git 历史，不再是"保留一个 Go 二进制" |
| 2 | **运行时模型** | ✅ **普通 ASP.NET Core + `net10.0`**（框架依赖发布）。NativeAOT 留作后续独立评估项，需单独的体积收益预算 |
| 3 | **是否立即动手** | ✅ **主体已完成**：`Nsst.Core` + `Nsst.Server` 建成，**134 个测试全绿**（Core 53 + Server 81），`dotnet build` **0 警告 0 错误**，`dotnet format --verify-no-changes` 干净，优雅关闭端到端验证，三条正确性行为实测通过 |
| 8 | **兼容目标** | ✅ **放弃与 Go 逐字节兼容**，改用原生 ASP.NET Core 惯用法。保留的只有前端真正消费的契约与三条正确性行为（见 §0.0） |
| 13 | **构建与发布链路** | ✅ `Dockerfile` / `Makefile` / 两个 workflow 全部改写完成（详见下方缺口 16：**改写完成 ≠ 验证过**） |
| 14 | **版本注入** | ✅ 已接通：`Directory.Build.props` 的 `<Version>` → SDK 写入 `AssemblyInformationalVersion` → `ServerConfig.ServiceVersion` 读取并去掉 `+<sha>` 后缀，`/api/health` 与 `/api/info` 如实返回。流水线用 `-p:Version=` 覆盖 |
| 15 | **前端产物目录** | ✅ 已挪到中立的 `web/dist`：`web/vite.config.ts` 的 `outDir`、`Nsst.Server.csproj` 的 `EmbeddedResource`、`.dockerignore` 三处同步改完，实测产物哈希与迁移前一致 |
| 16 | **Release 二进制策略** | ✅ **只发容器镜像**（原选项 a）。GitHub release 只保留自动生成的说明，不再挂二进制——框架依赖发布的产物是 IL，要单文件分发就得按 RID 自包含或 NativeAOT，那需要各平台原生 runner |

### 已知缺口（尚未做，按优先级）

| # | 缺口 | 说明 |
|---|---|---|
| 9 | **pacing 验收（§7.2）** | ❌ **完全未做**。三协议并发、60 秒、p99/p99.9、服务端 CPU/内存/GC 都还没测。**这是「最好的性能」唯一能被证明的地方**，也是当前最大的未知 |
| 17 | **镜像从未构建过** | ❌ Docker 守护进程当前未运行，**这份 .NET Dockerfile 一次都没有真正跑过**。多阶段、`UseAppHost=false`、非 root uid、HEALTHCHECK 全是照文档写的，未经验证。同理，镜像体积至今没有数字 |

### 仍需确认

| # | 决策 | 选项 | 何时必须定 |
|---|---|---|---|
| 6 | **pacing 阈值** | 第 9 项的具体通过标准。**注意：基准不再是与 Go 对比**（对比工具已删），需要独立定义绝对阈值，例如「p99 偏差 ≤ 2ms」 | **做第 9 项之前必须定** |
| 7 | **镜像体积上限** | 团队能接受的 MB 数 | Docker 守护进程可用之后 |

> 第 6 项的性质变了，这点要说清楚：以前它可以写成「不比 Go 差」，因为有个现成的对照物。
> 对比工具删掉之后，阈值必须**自己站得住**。如果等到测完再讨论「抖动算不算变差」，结论一定会退化成主观判断。

---

## 10. 附录 A：实测脚本（可复现）

§2 中的 Kestrel / `HostOptions` 默认值由下面的探针程序直接读取本机 .NET 10.0.12 共享框架得出，**不是从文档抄录**。团队评审时可直接重跑复核。

已经落进仓库、可以随时重跑的实测工具：

| 工具 | 用途 | 跑法 |
|---|---|---|
| `dotnet/spike/TimerResolution/` | §2.7 的定时器量化与高分辨率替代实测 | `dotnet run --project dotnet/spike/TimerResolution -c Release` |
| `dotnet/tests/Nsst.Core.Tests` | 975 条 golden 帧 + 50 条 golden 参数 + 流引擎/配额/计数器（53 个） | `dotnet test dotnet/Nsst.slnx -c Release` |
| `dotnet/tests/Nsst.Server.Tests` | 配置解析、时长/MIME/ClientIp 格式表、CORS、**优雅关闭端到端**（64 个） | 同上 |
| ~~`dotnet/parity/*.ps1`~~ | ~~与 Go 的逐字节对比（3 个脚本）~~ **已删除**，见 §0.0 | —— |

**删掉对比工具之后，帧格式靠什么保护？** 靠 `golden/frames.txt` 与 `golden/params.txt`：它们是**提交进仓库的固定数据**，不依赖 Go 运行时，也不需要 Go 服务器在旁边跑。而帧格式是前端真的在解析的东西（`sequence` / `server_time` / `payload_size` / `payload`），所以这层保护是必要的，没有随迁移方向一起丢。

原本 `compare-stream.ps1` 承担的另一半（pacing 分布对比）现在没有替代品——但这半本来就该由 §7.2 的验收来接，而不是由「和 Go 比」来接。

下面的探针是 §2.1～§2.3 的原始出处，保留全文以便复核。

```bash
# 建议放在仓库外，例如 %TEMP%\kestrelprobe
dotnet run -c Release
```

**`kestrelprobe.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

**`Program.cs`**

```csharp
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;

// 1) 运行期默认值
var k = new KestrelServerLimits();
static string Rate(MinDataRate r) =>
    r is null ? "<null / disabled>" : $"{r.BytesPerSecond} bytes/s, grace {r.GracePeriod}";

Console.WriteLine("=== KestrelServerLimits defaults ===");
Console.WriteLine($"MinResponseDataRate      = {Rate(k.MinResponseDataRate)}");
Console.WriteLine($"MinRequestBodyDataRate   = {Rate(k.MinRequestBodyDataRate)}");
Console.WriteLine($"RequestHeadersTimeout    = {k.RequestHeadersTimeout}");
Console.WriteLine($"KeepAliveTimeout         = {k.KeepAliveTimeout}");
Console.WriteLine($"MaxRequestHeadersTotalSize = {k.MaxRequestHeadersTotalSize}");
Console.WriteLine($"MaxResponseBufferSize    = {k.MaxResponseBufferSize}");
Console.WriteLine($"MaxRequestBodySize       = {k.MaxRequestBodySize}");

Console.WriteLine("=== HostOptions defaults ===");
Console.WriteLine($"ShutdownTimeout          = {new HostOptions().ShutdownTimeout}");

// 2) 关键 API 是否存在
Console.WriteLine("=== API availability ===");
Console.WriteLine($"Interlocked.Increment(ref ulong) = {typeof(Interlocked).GetMethod("Increment", [typeof(ulong).MakeByRefType()]) is not null}");
Console.WriteLine($"Ascii.IsValid                    = {typeof(Ascii).GetMethod("IsValid", [typeof(ReadOnlySpan<byte>)]) is not null}");
Console.WriteLine($"Rune.DecodeFromUtf8              = {typeof(System.Text.Rune).GetMethod("DecodeFromUtf8", [typeof(ReadOnlySpan<byte>), typeof(System.Text.Rune).MakeByRefType(), typeof(int).MakeByRefType()]) is not null}");

// 3) 逐帧写超时的唯一候选，以及响应体控制面
foreach (var name in new[]
{
    "Microsoft.AspNetCore.Server.Kestrel.Core.Features.IConnectionTimeoutFeature",
    "Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinResponseDataRateFeature",
    "Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature",
})
{
    Type t = null;
    foreach (var asm in new[] { "Microsoft.AspNetCore.Server.Kestrel.Core", "Microsoft.AspNetCore.Http.Features" })
    {
        try { t = Assembly.Load(asm).GetType(name); } catch { }
        if (t is not null) break;
    }
    if (t is null) { Console.WriteLine($"[absent]  {name}"); continue; }
    Console.WriteLine($"[present] {t.FullName}");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"            {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
}
```

**本次实测输出（.NET 10.0.12，Windows x64）**

```
=== KestrelServerLimits defaults ===
MinResponseDataRate      = 240 bytes/s, grace 00:00:05
MinRequestBodyDataRate   = 240 bytes/s, grace 00:00:05
RequestHeadersTimeout    = 00:00:30
KeepAliveTimeout         = 00:02:10
MaxRequestHeadersTotalSize = 32768
MaxResponseBufferSize    = 65536
MaxRequestBodySize       = 30000000
=== HostOptions defaults ===
ShutdownTimeout          = 00:00:30
[present] Microsoft.AspNetCore.Server.Kestrel.Core.Features.IConnectionTimeoutFeature
            Void SetTimeout(TimeSpan)
            Void ResetTimeout(TimeSpan)
            Void CancelTimeout()
[present] Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature
            Stream get_Stream()
            PipeWriter get_Writer()
            Void DisableBuffering()
            Task StartAsync(CancellationToken)
            Task SendFileAsync(String, Int64, Nullable`1, CancellationToken)
            Task CompleteAsync()
```

注意 `IHttpResponseBodyFeature` 的方法清单里**没有**任何 deadline / timeout 相关成员 —— 这是 §2.2 判断的直接依据。

---

## 11. 附录 B：内嵌正文的实测属性

```
pkg/protocol/payload.txt
SHA256 : D00718988BA3C1276CEC677900BEBE08F620D76E06396BA7055C384DED53F9BA
Bytes  : 8699
ASCII  : True
& = 0    < = 0    > = 0    \ = 0    " = 0
```

这组数字是 §2.4 的证据：Go 的自定义编码器不做 HTML 转义，而 `encoding/json` 会，
当前字节级比对测试之所以能过，**是因为内嵌正文恰好不触发这个分歧**。
建议把这份 hash 写进 Phase 2 的 golden-file 测试，作为基准输入固定下来。


---

## 12. 收尾建议

**先做 Phase 0 的三个 Spike，再决定要不要做这件事。**

Phase 0 大约 1～2 天，但它会回答"逐帧写超时能不能实现"和"镜像到底多大"这两个当前只能推测的问题。如果 Spike A 走不通、或者 Spike B 的体积超出接受范围，那这次迁移的性价比就需要重新讨论 —— 而这时候你只花了 1～2 天，而不是 4～6 周。

评审时建议优先讨论下面三个问题，它们都不是实现细节，而是会影响"这事值不值得做"的判断：

1. **§2.2** —— 能否接受"客户端不读时是整条连接被中止，而不是单次写返回错误"？这会改变 `/api/metrics` 里 `streams_errored` / `streams_cancelled` 的口径，也会改变 WebSocket 关闭码的选择。
2. **§7.2** —— pacing 劣化的容忍阈值具体是多少？这个数字必须现在定死。留到 Phase 7 再讨论，结论一定会退化成"感觉还行"。
3. **§6 补充** —— 放弃"5 平台自包含二进制"是否可以接受？这是 Go 免费提供、而 .NET 需要额外成本（原生 runner 或 QEMU）的能力。

如果这三个问题里有一个答案是否定的，那么更划算的方案可能是：**保留现有 Go 服务，把 C# 投入到团队效率真正能产生复利的地方**。

需要把这一点写清楚：现有实现约 2,600 行、89 个测试、镜像 21 MB，已经工作良好，而且这个项目的核心价值本身就是**测量精度**。为了统一技术栈而重写它，收益是团队认知一致性，成本是 4～6 周、镜像变大 2～3 倍、多架构构建变复杂、以及 pacing 可能劣化的风险。这是一个**合理的权衡**，但应该是一个被明确算过账的决定，而不是一个默认选项。
