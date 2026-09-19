# 后端选型对比：Go / ASP.NET Core / TypeScript

> 本文是 `docs/MIGRATION-ASPNET.md` 的配套文档，把"换成 C#"这个单一选项扩展成三方案对比。
>
> 标注 **[实测]** 的结论来自本机真实运行；标注 **[估计]** 的是推断，尚未验证（Docker daemon 未运行，无法测量镜像体积）。
>
> **状态：已决策，本文转为决策记录。** 最终选了 **ASP.NET Core**：Go 树已从仓库删除，`dotnet/` 是唯一实现。本文保留的价值在于记录**当时是怎么权衡的**——尤其是下面这条容易被忽略的观察（语言数量其实不会因为换 C# 而减少），以及为什么最后还是换了。

---

## 0. 先纠正一个前提

前两轮的讨论默认了一个框架：*"要么 Go，要么 C#"*。但如果真正的动机是**减少语言数量、发挥团队主力语言的优势**，那么 C# 并不解决这个问题。

因为 **这个项目已经有一半是 TypeScript 了。**

| 方案 | 后端 | 前端 | 语言数量 |
|---|---|---|---|
| 现状 | Go | TS | **2** |
| 换 C# | C# | TS | **2** |
| 全栈 TS | TS | TS | **1** |

C# 方案只是**把两个语言中的一个换掉**，语言数量不变、上下文切换成本不变、前端那 18 个 TS 源文件一行都不会少。

所以只有两个问题是真正要回答的：

1. **目标是"一种语言"，还是"必须用 C#"？**
   - 前者 → 全栈 TS 才是自洽的答案，C# 不解决它。
   - 后者 → 那是偏好，本文下半部分的技术分析仍然适用，但结论要按偏好加权。

2. **哪个方案对这个"测量工具"的测量精度伤害最小？**

第 2 点是本文的重点。

---

## 1. 实测数据

### 1.1 Node 运行时默认值 **[实测]**

测于 **Node v26.7.0**（`http.createServer()` 的实例属性）：

| 项 | Node 默认 | Go 现状 | Kestrel 默认 |
|---|---|---|---|
| 请求超时 | `requestTimeout = 300000`（5 分钟） | 无 | `RequestHeadersTimeout = 30s` |
| 头部超时 | `headersTimeout = 60000` | `ReadHeaderTimeout = 10s` | `RequestHeadersTimeout = 30s` |
| 长连接空闲 | `keepAliveTimeout = 5000` | `IdleTimeout = 120s` | `KeepAliveTimeout = 130s` |
| 整体超时 | `timeout = 0`（**禁用**） | 不设 | 不设 |
| 最小响应速率 | **无此概念** | 无 | **`MinResponseDataRate = 240 B/s`** ⚠️ |

### 1.2 两个关键机制，都已验证 **[实测]**

**(a) `requestTimeout` 会杀掉长响应流吗？→ 不会。**

我的初始怀疑是"5 分钟默认值 < `MAX_DURATION` 默认 3600 秒，必然出事"。实测推翻了它：

```
server.requestTimeout  = 1000 ms      # 故意设成远短于响应时长
server.headersTimeout  = 1000 ms
响应时长                = 5000 ms      # 10 帧 @ 500ms

结果: RESULT: completed normally  frames=10  elapsed=5134ms
```

`requestTimeout` 管的是"接收完整请求"，而 GET 无请求体，头部一到计时器就清了，**不影响响应阶段**。这条我猜错了，如实记录。

**(b) 客户端停止读取时，能踢掉它吗？→ 能。**

Node 没有写超时（类比 Go 的 `SetWriteDeadline`）。候选机制是 `socket.setTimeout()`（socket 活动超时）。实测：

```
server: socket.setTimeout(1000) + 持续写 64 KiB 帧
client: 发完请求后完全不读

[server] backpressure first observed at frame 1 (0.1 MiB), 46ms — res.write() returned false
[server] socket inactivity timeout fired after 1077ms
=> Node CAN enforce a write deadline via socket.setTimeout()
[server] response closed after 85 frames (5.3 MiB)
peak rss = 61 MiB
```

结论：**写超时可手搓，且内存上界可控**（背压 46ms 就出现，超时 1077ms 触发，RSS 稳定在 61 MiB）。

### 1.3 一个 Node 相对 Kestrel 的真实优势 **[实测]**

`docs/MIGRATION-ASPNET.md` §2.1 记录的 Kestrel 坑 —— `MinResponseDataRate = 240 B/s`、宽限 5 秒、低于就中止连接 —— 会掐死本项目 README 里明确记录的合法用法（`payload_size=0&interval=250ms` ≈ 200 B/s）。

**Node 没有这个概念。** 上面的测试用的是 500ms 间隔、约 50 字节的帧（≈ 100 B/s），远低于 240 B/s，Node 正常跑完。

Kestrel 需要一个显式的 `MinResponseDataRate = null` 才能避免；Node 不需要做任何事。

### 1.4 已存在的类型漂移（现成的证据）**[实测]**

`web/src/types.ts` 手工镜像了 **9 个**后端类型表面：

| TS 类型 | 对应 Go |
|---|---|
| `StreamFrame` | `pkg/protocol.Frame` |
| `Protocol` / `PROTOCOLS` | `internal/api.Protocols` + 三个 handler 的 `Protocol` 常量 |
| `Limits` | `internal/api.limitsView` |
| `Defaults` | `internal/api.defaultsView` |
| `ConfigResponse` | `internal/api.configResponse` |
| `InfoResponse` | `internal/api.infoResponse` |
| `HealthResponse` | `internal/api.healthResponse` |
| `ClientResponse` | `internal/api.clientResponse` |
| `MetricsResponse` | `internal/metrics.Snapshot` |

**没有任何测试能发现它们漂移** —— 因为两份定义在两种语言里。而漂移**已经发生了**：

```ts
// web/src/types.ts:16-18
/** Number of payload bytes; the payload is omitted when this is zero. */
payload_size: number;
/** Fill bytes ('a' repeated); present only when payload_size > 0. */
payload?: string;
```

但真实负载**不是** `'a'` 重复，而是 `pkg/protocol/payload.txt` 里那篇英文说明文。README 第 487 行明确写着：

> `payload` 是 `pkg/protocol/payload.txt` 里那篇英文说明文的一段，**不是重复的填充字符**

**契约文档写错了，而且错在最不该错的地方**（前端同学照着这个注释理解线格式，会得到错误的心智模型）。

同一文件第 94 行还有一处值得注意：

```ts
/** "HTTP/1.1" or "HTTP/2.0". */
proto: string;
```

它把 Go 的协议字符串格式固化进了前端契约。这正是 `MIGRATION-ASPNET.md` §6 风险 6 记的那件事：**ASP.NET Core 的 `HttpRequest.Protocol` 返回 `"HTTP/2"` 而不是 `"HTTP/2.0"`**，换后端就会破坏这个字段。

全栈 TS 让这两类问题**在结构上不可能发生**：`Frame` 只有一个定义，两边 import 同一个。

---

## 2. 能力对比

| | Go（现状） | C# / ASP.NET Core | TS / Node |
|---|---|---|---|
| 镜像体积 | **21.3 MB**（README 实测） | ~50–70 MB **[估计]** | ~50–60 MB **[估计]** |
| 语言数量 | 2 | 2 | **1** |
| 跨架构构建 | 交叉编译（无 QEMU） | IL 可跨 RID 发布（无 QEMU） | **无需构建差异** |
| 发布产物 | 5 个 ~10 MB 静态二进制 | 需重新设计 | JS bundle + **要求装 Node** |
| 逐帧写超时 | 原生支持 | 可补（`CancelAfter` + 异常过滤器，**更精确**） | 可补（`socket.setTimeout`，**[实测] 可行**） |
| 最小数据速率杀手 | 无 | ⚠️ **240 B/s，必须显式关闭** | **无**（**[实测]** 确认） |
| 内嵌前端 | `go:embed` 原生 | `EmbeddedResource` 等价 | esbuild 内联 / 随镜像分发 |
| 并发模型 | goroutine，**多核并行** | 线程池，**多核并行** | ⚠️ **单事件循环，串行** |
| 竞态检测 | `go test -race` | 无 | 无（但真并行也很少） |
| 类型共享 | 手工镜像（**已漂移**） | 手工镜像 | **单一来源** |
| 字节处理 | `[]byte`，需与 `string` 划清界限 | UTF-16 `string`，**最别扭** | `Buffer` 即字节，**最自然** |
| 启动 / 内存 | 极快 / ~10–15 MB | 快 / ~30–60 MB | 快 / ~40–60 MB **[实测 61 MiB]** |
| 测量抖动风险 | **低** | 中（GC，可调） | **中高（结构性串行）** |
| 团队现状 | — | **偏好** | 前端已是 TS |

---

## 3. TS 方案赢在哪

1. **类型单一来源** —— §1.4 那 9 个手工镜像表面全部消失，契约漂移在结构上不可能发生。这个项目**整个对外契约就是 JSON over HTTP**，收益是全覆盖的。

2. **`Buffer` 就是字节** —— Go 最难的那部分代码（`pkg/protocol/payload.go` 的 rune 边界对齐 + JSON 转义 + 环绕切分）移植到 TS **比移植到 C# 自然得多**。C# 要用 `byte[]` 并时刻提防别掉进 UTF-16 `string`；TS 里 `Buffer` 天生就是字节序列，`buf.toString('utf8')` 也按 UTF-8 语义工作。

3. **多架构零成本** —— 纯 JS 没有架构相关的构建产物，**一份 bundle 同时跑 amd64 和 arm64**。这比 Go 的交叉编译更省事，比 .NET 的 per-RID 发布更是简单一个量级。Dockerfile 和 CI 都显著变简单。
   （前提：不引入 `bufferutil` / `utf-8-validate` 这类 `ws` 的可选原生依赖，保持纯 JS。）

4. **没有运行时陷阱** —— §1.3：不需要像 Kestrel 那样去关一个默认会掐死流的配置。

5. **工具链统一** —— pnpm、eslint、tsc、vitest、一套 CI。前端的 `npm ci` 流程和后端复用。

6. **`-race` 的损失大幅缩水** —— Node 单 isolate 单线程，**并发流之间没有共享可变内存**（除非上 `worker_threads` + `SharedArrayBuffer`）。竞态从"内存模型竞态"退化成"异步交错逻辑错误"。所以：丢了 `-race`，但同时也没什么可 race 的。
   （代价：异步交错 bug 依然真实，而且比内存竞态更难用工具捕获。）

---

## 4. TS 方案输在哪

### 4.1 单线程事件循环 —— 这是唯一的结构性问题

Go：100 条并发流 = 100 个 goroutine，跨所有核心调度。
C#：线程池，真正并行。
Node：**所有流共享一个事件循环。**

先做个诚实的定量校准，避免夸大：

```
每帧 CPU 成本 ≈ 编码 JSON + 写 socket ≈ 数微秒
100 条流 × 100 帧/秒 = 10,000 帧/秒
10,000 × 5µs = 50ms CPU / 秒 ≈ 单核的 5%
```

**所以瓶颈不是 CPU 吞吐，事件循环有大量余量。**

真正的问题是另外两个：

**(a) 单帧慢操作会卡住所有流。** `MAX_PAYLOAD_SIZE` 允许到 64 MB，默认 1 MiB。在事件循环上 JSON 转义一个 1 MiB 负载可能耗时数毫秒 —— **在这几毫秒里，其他 99 条流全部停摆**。

而这个工具的职责恰恰是在客户端测量帧间隔。服务端的这种停顿，在客户端看来**与网络抖动完全无法区分**。Go 和 C# 里这个编码发生在线程/goroutine 上，不会阻塞别人。

**(b) 定时器串行派发。** 100 个到期时间接近的定时器被依次处理，第 100 个会晚于第 1 个。因为本项目 pacer 用**绝对网格**（迟到就重对齐、不补发），单条流的间隔精度可以自校正；但定时器整体的派发延迟会随并发数增长。

**缓解手段与其代价：**

| 手段 | 代价 |
|---|---|
| 限制 `payload_size` 上界（如 256 KiB） | 削弱工具的测试能力，改变对外契约 |
| 用 `worker_threads` 分片跑流 | 计数器要换成 `SharedArrayBuffer` + `Atomics`；并发配额变成跨 worker 协调 —— **复杂度显著上升，且容易出错** |
| 预算转义结果 / 分块编码让单帧成本有界 | 需要维护偏移映射（Go 注释里说明过为什么不能简单预计算：块边界可能落在转义序列中间） |
| 接受并在 `MAX_CONCURRENT_STREAMS` / `MAX_PAYLOAD_SIZE` 上给出保守默认 | 诚实但降低能力 |

**校准结论：** 在真实使用场景（3 个协议、一个开发者、或 CI 里 3 条流）下，Node **完全够用**，事件循环根本不构成问题。风险只出现在配置范围的上端（`MAX_CONCURRENT_STREAMS=100` 且 `interval=10ms` 且大 `payload_size`）。这是一个"你必须知道上界在哪"的问题，不是"它做不到"的问题。

但它和 .NET 的 GC 问题属于同一类 —— 所以 `MIGRATION-ASPNET.md` §7.2 的 **pacing 验收测试对 TS 同样适用，而且更必要**。

### 4.2 分发与镜像

- `node:22-alpine` 基础镜像约 130 MB 未压缩 / ~45 MB 压缩，加 `ws` + `dist` → ~50–60 MB **[估计]**，约是 Go 的 2.5 倍（与 .NET 大致相当）。
- `bun build --compile` / `deno compile` 能出单文件，但产物 ~50–90 MB，**并不更小**。
- Node 26 有内置 WebSocket **客户端**，但没有服务端。要么用 `ws`（纯 JS，约 100 KB），要么手搓 RFC 6455（约 400 行）。
- **发布产物是实打实的降级**：Go 给的是 5 个 ~10 MB 的自包含静态二进制，`curl | tar` 就能跑。TS 方案给的是需要预装 Node 运行时的 JS bundle。对一个"网络诊断工具"来说，这是明显更差的 UX。

### 4.3 内存

基线 RSS 约 40–60 MB（实测 61 MiB），Go 同等工作负载约 10–15 MB。对一个长期运行的服务可接受，但不是优势。

### 4.4 类型在运行时被擦除

TS 类型编译后消失。共享类型能防住"前后端定义不一致"，但**防不住"后端实际发的东西不符合类型"**。请求参数校验仍需显式实现 —— 这部分其实和 Go 的 `ParseParams` 一样，可以配 zod / valibot 做，**未必更差**。

---

## 5. 关于 C# 方案的一个补充观察

不是要否定它，而是把前面漏掉的一条说清楚：

`MIGRATION-ASPNET.md` §8 的结论（10 项能力里 9 项能补、只有 `-race` 补不回来）依然成立，**而且 C# 在写超时上比 TS 更精确** —— `when (!ct.IsCancellationRequested)` 这个异常过滤器能干净区分"写超时"和"对端断开"，而 Node 的 `socket.setTimeout` 只能告诉你 socket 不活动了。

C# 还有 TS 没有的：**真正的多核并行**，所以 §4.1 那个"单帧卡住所有流"的问题不存在。

C# 的真正短板不是技术，是**它不解决语言数量问题**（§0），以及生态收益在本项目里大部分用不上（`MIGRATION-ASPNET.md` §8.4）。

---

## 6. 结论

### 三个方案的定位

| 方案 | 适用条件 |
|---|---|
| **保持 Go** | 目标是"风险最低"。工具已工作、21 MB、89 个测试、`-race` 干净。**什么都不用做。** |
| **全栈 TS** | 目标是"一种语言 / 单一契约来源"。技术上可行，两处硬骨头（写超时、Buffer 字节处理）**都比 C# 路径更顺**，多架构还更简单。代价是把 pacing 风险放在结构性更差的位置。 |
| **C#** | 团队明确要 C#，且会长期用它维护这个服务。技术上没有阻塞项，写超时甚至更精确，但**不减少语言数量**。 |

### 如果必须在 TS 和 C# 之间选

按本项目最看重的"测量精度"排序，**C# 略优**：它有真正的多核并行，而 TS 把所有流串行在一个事件循环上；且写超时的分类比 Node 更干净。

按"这个项目已经有一半是 TS"和"契约单一来源"排序，**TS 略优**：它消除 9 处手工镜像（其中一处已经漂移），并且多架构构建明显更简单。

**两者的镜像体积和发布产物降级大致相同**，都不是决定性差异。

### 我的建议

1. **如果没有强烈的"必须统一到某一种语言"的理由，就保持 Go。** 现有实现工作良好，而且这个产品的核心价值就是测量精度 —— 换后端是在一个已经正确的地方引入风险。

2. **如果团队确实想收敛到一种语言，那应该认真评估全栈 TS，而不是默认选 C#。** 因为 C# 不解决"语言收敛"这个目标，它只是换掉了两个语言中的一个。而且 TS 路径上有两个技术点（`Buffer` 字节处理、多架构零成本）比 C# 更顺。

3. **无论选哪个，Phase 7 的 pacing 验收测试都不能省。** 三种方案的差别最终都会落到这一个数字上：在 `interval=10ms` 下，p99 帧间隔和超阈值次数有没有变差。这是唯一能把"要不要迁移"从偏好变成可证伪实验的东西。

---

## 附录：实测脚本

### A. Node HTTP 默认值

```bash
node -e "const http=require('http');const s=http.createServer();\
console.log('requestTimeout  =',s.requestTimeout);\
console.log('headersTimeout  =',s.headersTimeout);\
console.log('keepAliveTimeout=',s.keepAliveTimeout);\
console.log('timeout         =',s.timeout);console.log(process.version)"
```

输出（Node v26.7.0）：

```
requestTimeout  = 300000
headersTimeout  = 60000
keepAliveTimeout= 5000
timeout         = 0
```

### B. `requestTimeout` 是否杀长响应

服务器 `requestTimeout = 1000`，响应持续 5000 ms（10 帧 @ 500 ms）：

```javascript
const http = require('http');
const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
  let n = 0;
  const timer = setInterval(() => {
    n += 1;
    res.write(JSON.stringify({ sequence: n, server_time: Date.now(), payload_size: 0 }) + '\n');
    if (n === 10) { clearInterval(timer); res.end(); }
  }, 500);
});
server.requestTimeout = 1000;
server.headersTimeout = 1000;
server.keepAliveTimeout = 1000;
server.listen(0, '127.0.0.1', () => {
  const port = server.address().port;
  const t0 = Date.now();
  let frames = 0;
  http.get({ host: '127.0.0.1', port, path: '/api/stream/http' }, (res) => {
    res.on('data', (b) => { frames += b.toString().split('\n').filter(Boolean).length; });
    res.on('end', () => {
      console.log(`completed frames=${frames} elapsed=${Date.now() - t0}ms`);
      process.exit(0);
    });
    res.on('aborted', () => {
      console.log(`ABORTED frames=${frames} elapsed=${Date.now() - t0}ms`);
      process.exit(0);
    });
  });
});
```

输出：

```
completed frames=10 elapsed=5134ms
```

### C. `socket.setTimeout` 能否踢掉停止读取的客户端

服务端 `socket.setTimeout(1000)` 并持续写 64 KiB 帧；客户端发完请求后**完全不读**：

```javascript
const http = require('http');
const net = require('net');
const t0 = Date.now();

const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
  const sock = res.socket;
  sock.setTimeout(1000, () => {
    console.log(`socket inactivity timeout fired after ${Date.now() - t0}ms`);
    sock.destroy();
  });
  const frame = Buffer.alloc(64 * 1024, 0x61);
  const timer = setInterval(() => res.write(frame), 1);
  res.on('close', () => clearInterval(timer));
});

server.listen(0, '127.0.0.1', () => {
  const sock = net.connect(server.address().port, '127.0.0.1', () => {
    sock.write('GET /api/stream/http HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n');
    // 故意不挂 data 处理器、不 resume()：客户端在请求发出后就不再读取
  });
  sock.on('error', () => {});
});
setTimeout(() => {
  console.log(`peak rss = ${(process.memoryUsage().rss / 1048576).toFixed(0)} MiB`);
  process.exit(0);
}, 12000);
```

输出：

```
[server] backpressure first observed at frame 1 (0.1 MiB), 46ms
[server] socket inactivity timeout fired after 1077ms
peak rss = 61 MiB
```
