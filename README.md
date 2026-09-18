# NSST — Network Stream Stability Tester

[![CI](https://github.com/higashitaniyume/nsst/actions/workflows/ci.yml/badge.svg)](https://github.com/higashitaniyume/nsst/actions/workflows/ci.yml)
[![Release](https://github.com/higashitaniyume/nsst/actions/workflows/release.yml/badge.svg)](https://github.com/higashitaniyume/nsst/actions/workflows/release.yml)

一个用来测量**长连接流式接口稳定性**的测试工具。它同时用三种协议连接同一台服务端，把每一帧的到达情况记录下来，然后告诉你：这条流到底稳不稳、什么时候卡了、卡了多久。默认用大白话给结论，也可以一键切到包含六张表和三张图的专业视图。

三种协议共用同一套帧格式，所以横向对比是有意义的：

| 协议 | 端点 | 传输方式 |
| --- | --- | --- |
| HTTP Streaming | `/api/stream/http` | `application/x-ndjson`，chunked，一帧一行 JSON |
| Server-Sent Events | `/api/stream/sse` | `text/event-stream`，`id:` + `event:` + `data:` |
| WebSocket | `/api/stream/ws` | 文本帧，一条消息一帧 |

单个 Go 二进制同时提供 Web 前端、REST API 和三个流式端点。没有数据库、没有 Redis、没有外部依赖、没有账号系统。

---

## 目录

- [重要：流中断 ≠ TCP 丢包](#重要流中断--tcp-丢包)
- [快速开始](#快速开始)
- [界面说明](#界面说明)
- [API 文档](#api-文档)
- [请求参数与限制](#请求参数与限制)
- [环境变量](#环境变量)
- [三种协议的实现细节](#三种协议的实现细节)
- [指标定义](#指标定义)
- [项目结构](#项目结构)
- [测试](#测试)
- [发布流程](#发布流程)
- [设计取舍](#设计取舍)
- [常见问题](#常见问题)
- [License](#license)

---

## 重要：流中断 ≠ TCP 丢包

这是整个工具最重要的一个约定，请先读这一段。

浏览器里的 JavaScript **看不到 TCP 层**。它拿不到丢包计数、重传次数、RTT，也拿不到 TCP 拥塞窗口。它能观察到的只有一件事：

> **两帧数据到达应用层的时间间隔。**

因此本工具从头到尾只报告 **Application / Stream Gap（应用层流间隙）**，绝不把它叫做「丢包」。一次中断可能来自完全不同的原因：

- 服务端生产/写入变慢（GC、锁竞争、磁盘抖动）
- 代理或负载均衡缓冲了响应（这也是 SSE 要发 `X-Accel-Buffering: no` 的原因）
- 网络路径出现拥塞、重传（**可能**是丢包，但也可能是无线重传、路由抖动）
- 客户端主线程被阻塞，读不到已经到达的数据

要区分这些，需要抓包（tcpdump/Wireshark）、服务端指标或 eBPF——那是另一个层面的工具。**本工具不猜，只报告它真正测到的东西。**

所以界面上你看到的是「中断次数」「最长流间隙」，而不是「丢包率」。

---

## 快速开始

### 方式一：Docker（推荐）

```bash
# 构建（多阶段：Node 构建前端 → Go 编译并内嵌前端 → Alpine 运行时）
docker build -t hyumerin/nsst .

# 运行
docker run -d --name streamtest -p 8080:8080 hyumerin/nsst

# 打开 http://localhost:8080
```

或者用 Compose（仓库根目录的 `docker-compose.yml`，服务起在 **http://localhost:55537**）：

```bash
docker compose up -d             # 拉取已发布的镜像并运行（不重新构建）
docker compose up -d --build     # 从源码构建后运行
docker compose logs -f
docker compose down
```

`docker-compose.yml` 里同时写了 `build:` 和 `image:`，所以 `up -d` 与 `up -d --build` 产出的镜像同名，两条路径不会互相污染。改端口不用编辑文件：

```bash
NSST_PORT=60000 docker compose up -d
```

镜像约 21 MB，进程以非 root（uid 10001）运行，自带 `HEALTHCHECK`。

构建 amd64 + arm64 双架构镜像：

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t hyumerin/nsst --push .
```

> **构建为什么快**：`web` 和 `build` 两个阶段固定在 `$BUILDPLATFORM` 上执行。前端产物和 Go 二进制都与宿主架构无关（`CGO_ENABLED=0`），所以它们**只在原生架构上跑一次**，再由 Go 交叉编译出目标架构——构建 arm64 镜像时 `npm` 和 Go 编译器都不会跑在 QEMU 模拟里。Go 的编译缓存和模块缓存用 BuildKit cache mount 挂载，改了代码也只重新编译受影响的包，而不是整个项目。

### 方式二：本地开发

需要 Go 1.24+ 和 Node 18+。

```bash
# 1. 构建前端（产物写进 internal/webui/dist，会被 go:embed 打包进二进制）
cd web && npm install && npm run build && cd ..

# 2. 构建并启动后端
go build -o bin/streamtest ./cmd/server
./bin/streamtest
```

打开 **http://localhost:8080**。

前端热更新模式（改 UI 用）：

```bash
# 终端 1
go run ./cmd/server        # :8080

# 终端 2
cd web && npm run dev      # :5173，/api 已代理到 8080（含 WebSocket）
```

访问 http://localhost:5173。注意：`go build` 不会重新打包前端，改了 TS 必须重跑 `npm run build`。

> **关于 `internal/webui/dist`**：这个目录是 `npm run build` 的产物，但**被提交进仓库**。原因是 `//go:embed all:dist` 在目录不存在时会直接编译失败，如果不提交，单纯 clone 下来跑 `go build ./...` 或 `go test ./...` 会报 `pattern all:dist: no matching files found`。Docker 构建会忽略这份提交内容并从 `web/` 源码重新生成。

---

## 界面说明

打开页面后**不需要点任何按钮**，它会立刻用服务端默认参数开始测试三种协议，并且**一直跑下去，直到你点「停止全部测试」**。首次自动测试默认勾选**断线自动重连**。

界面有两套视图，右上角按钮随时切换，选择记在 `localStorage`：

| 视图 | 面向 | 内容 |
| --- | --- | --- |
| **通俗视图**（默认） | 不懂网络的人 | 三张卡片，用大白话说清每种方式稳不稳；图表、事件日志、指标说明全部收起 |
| **专业视图** | 排查问题的人 | 六张表 + 三张图 + 事件日志 + 指标定义 |

### 通俗视图

顶部先给一句总结，下面每个协议一张卡片：

| 卡片上的说法 | 实际含义 |
| --- | --- |
| 收到了多少条 | 实际解码成功的帧数 |
| 数据一共多大 | 应用层字节数，用 KB / MB 表示 |
| 大概多久来一条 | 相邻帧到达间隔的平均值 |
| 中途卡了几次 | 超过阈值的间隙个数 |
| 最长卡了多久 | 最严重的一次卡顿时长 |
| 连接断过吗 | 断线次数（开了自动重连并成功恢复的不算断） |

卡片右上角是结论：很流畅 / 偶尔慢一下 / 经常卡顿 / 连不上 / 断开了。这一层**刻意不出现「丢包」字样**，原因见[重要：流中断 ≠ TCP 丢包](#重要流中断--tcp-丢包)。

### 专业视图：六张表

每个协议两张表：

**① 连接与流量**

| 指标 | 含义 |
| --- | --- |
| 状态 | 空闲 / 连接中 / 已连接 / 已完成 / 已断开 / 错误 |
| 收到帧数 | 实际解码成功的帧数 |
| 接收数据量 | 应用层字节数（不含 IP/TCP/TLS/分帧开销） |
| 平均吞吐 | 整个运行期间的平均字节率 |
| 当前吞吐 | 最近 2 秒窗口的字节率 |
| 首帧延迟 | 从发起到第一帧到达的时间 |
| 帧完整性 | 用 `payload_size` 校验负载长度是否被截断或改写 |

**② 时延与间隔**

| 指标 | 含义 |
| --- | --- |
| 目标间隔 | 本次请求的 `interval` |
| 平均 / 最小 / 最大间隔 | 相邻两帧到达间隔的统计量 |
| 最长流间隙 | 超过阈值的最大间隙 |
| 中断次数 | 超过阈值的间隙个数 |
| 丢帧数 | 由 `sequence` 不连续推导（仅在同一条连接内统计） |
| 重连次数 | 仅在开启自动重连且连接确实断过时非零 |

### 三张图

帧间隔、吞吐、中断点三条曲线**分别是三种协议**，共用同一条时间轴，所以能直接看出哪个协议先出问题。中断点图把每次超阈值的间隙按它**结束的时刻**打点。

### 停止之后

点「停止全部测试」后，六张表冻结在最终读数，参数面板**解锁**。这时才能改协议、时长、间隔、负载，然后点「开始测试」跑新一轮。测试进行中所有参数控件都是禁用的，避免测到一半改参数导致结果无意义。

### 中英文切换

右上角按钮切换中英文。默认跟随浏览器语言，选择记在 `localStorage`。所有文案（包括运行时的错误信息，以及通俗视图里的每一句话）都会切换；切换语言不会打断正在进行的测试。

---

## API 文档

所有接口同源，不需要认证。

### `GET /api/health`

存活探测，适合做健康检查。

```json
{"status":"ok","version":"1.0.0","uptime_seconds":42}
```

### `GET /api/info`

服务信息、端点表、当前限制和默认值。

```json
{
  "name": "StreamTest",
  "version": "1.0.0",
  "protocols": ["http-stream", "sse", "websocket"],
  "endpoints": {
    "health": "/api/health",
    "info": "/api/info",
    "config": "/api/config",
    "metrics": "/api/metrics",
    "http": "/api/stream/http",
    "sse": "/api/stream/sse",
    "websocket": "/api/stream/ws"
  },
  "uptime_seconds": 42,
  "active_streams": 3,
  "limits": {
    "max_duration": 3600,
    "min_duration": 1,
    "max_interval": 60000,
    "min_interval": 10,
    "max_payload_size": 1048576,
    "max_concurrent_streams": 100,
    "write_timeout_ms": 15000
  },
  "defaults": {"duration": 60, "interval": 100, "payload_size": 4096}
}
```

### `GET /api/config`

限制与默认值，供前端在启动前做参数校验。

### `GET /api/metrics`

服务端累计计数。

```json
{
  "active_streams": 3,
  "streams_started": 12,
  "streams_finished": 9,
  "streams_cancelled": 1,
  "streams_errored": 0,
  "streams_rejected": 0,
  "frames_sent": 4210,
  "bytes_sent": 17301504
}
```

### `GET /api/stream/http`

NDJSON 流，一帧一行，写一帧 flush 一次。

```bash
curl -N "http://localhost:8080/api/stream/http?duration=2&interval=250&payload_size=16"
```

```
{"sequence":1,"server_time":1789659795314,"payload_size":16,"payload":"aaaaaaaaaaaaaaaa"}
{"sequence":2,"server_time":1789659795564,"payload_size":16,"payload":"aaaaaaaaaaaaaaaa"}
{"sequence":3,"server_time":1789659795814,"payload_size":16,"payload":"aaaaaaaaaaaaaaaa"}
{"sequence":4,"server_time":1789659796064,"payload_size":16,"payload":"aaaaaaaaaaaaaaaa"}
```

响应头：`Content-Type: application/x-ndjson`、`Cache-Control: no-store`。

### `GET /api/stream/sse`

标准 SSE。连接建立后先发一个注释行用于立刻触发 flush 和代理放行，然后每帧一个事件。

```bash
curl -N "http://localhost:8080/api/stream/sse?duration=1&interval=250&payload_size=0"
```

```
: stream open

id: 1
event: data
data: {"sequence":1,"server_time":1789659796346,"payload_size":0}

id: 2
event: data
data: {"sequence":2,"server_time":1789659796597,"payload_size":0}
```

响应头：`Content-Type: text/event-stream`、`Cache-Control: no-cache`、`Connection: keep-alive`、`X-Accel-Buffering: no`。空闲超过 15 秒会发 `: keep-alive` 心跳。

### `GET /api/stream/ws`

WebSocket。一次 GET 升级即可：

```bash
# 只看握手结果
curl -i -N --max-time 3 \
  -H "Connection: Upgrade" -H "Upgrade: websocket" \
  -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" \
  "http://localhost:8080/api/stream/ws?duration=1&interval=500"
```

```
HTTP/1.1 101 Switching Protocols
Upgrade: websocket
Connection: Upgrade
Sec-Websocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=
```

浏览器控制台里可以直接看：

```js
const ws = new WebSocket('ws://localhost:8080/api/stream/ws?duration=5&interval=100&payload_size=1024');
const t = []; let n = 0;
ws.onmessage = (e) => {
  const f = JSON.parse(e.data); t.push(performance.now()); n++;
  console.log(f.sequence, f.payload_size, t.length > 1 ? (t.at(-1) - t.at(-2)).toFixed(1) + 'ms' : '');
};
ws.onclose = (e) => console.log('closed', e.code, e.reason, 'frames=' + n);
```

正常跑完会收到关闭码 `1000`、原因 `stream completed`。不协商任何扩展、不压缩。

### 错误响应

所有错误都是同一个 JSON 信封：

```json
{"error":"invalid_parameter","message":"parameter \"interval\": must be at least 10 milliseconds (got \"1\")","param":"interval"}
```

| 状态码 | `error` | 什么时候 |
| --- | --- | --- |
| 400 | `invalid_parameter` | 参数缺失、非法或超限 |
| 404 | `not_found` | 未知的 `/api/` 路径 |
| 405 | `method_not_allowed` | 路径存在但方法不对（带 `Allow` 头） |
| 503 | `too_many_streams` | 并发流达到上限（带 `Retry-After`） |
| 503 | `server_shutting_down` | 服务正在优雅关闭 |

---

## 请求参数与限制

三个流式端点共用同一组查询参数：

| 参数 | 单位 | 默认值 | 合法范围 | 说明 |
| --- | --- | --- | --- | --- |
| `duration` | 秒 | 60 | 1 – `MAX_DURATION`（默认 3600） | 推送时长 |
| `interval` | 毫秒 | 100 | `MIN_INTERVAL` – `MAX_INTERVAL`（默认 10 – 60000） | 帧间隔 |
| `payload_size` | 字节 | 4096 | 0 – `MAX_PAYLOAD_SIZE`（默认 1048576） | 每帧负载大小，0 表示空负载 |

帧数是确定的：`ceil(duration * 1000 / interval)`。例如 `duration=1&interval=250` 正好 4 帧。

`payload_size=0` 时帧里不出现 `payload` 字段（只有 3 个字段）；大于 0 时填充 `'a'` 字符，不做 JSON 转义。

---

## 环境变量

全部可选，都有安全默认值。非法值会**直接启动失败并报错**，而不是被静默忽略。

| 变量 | 默认值 | 范围 | 说明 |
| --- | --- | --- | --- |
| `HOST` | （空 = 全部网卡） | — | 监听地址 |
| `PORT` | `8080` | 1 – 65535 | 监听端口 |
| `MAX_DURATION` | `3600` | 1 – 86400 | 最长时长（秒） |
| `MAX_PAYLOAD_SIZE` | `1048576` | 0 – 67108864 | 最大负载（字节） |
| `MIN_INTERVAL` | `10` | 1 – 60000 | 最小间隔（毫秒） |
| `MAX_INTERVAL` | `60000` | 1 – 3600000 | 最大间隔（毫秒） |
| `MAX_CONCURRENT_STREAMS` | `100` | 1 – 100000 | 并发流上限 |
| `WRITE_TIMEOUT_MS` | `15000` | 100 – 600000 | 单帧写超时（毫秒） |
| `SHUTDOWN_TIMEOUT` | `15` | 1 – 600 | 优雅关闭排空时长（秒） |
| `CORS_ALLOW_ORIGINS` | （空 = 仅同源） | 逗号分隔或 `*` | 允许的跨域来源 |
| `LOG_LEVEL` | `info` | `debug`/`info`/`warn`/`error` | 日志级别 |
| `LOG_FORMAT` | `text` | `text`/`json` | 日志格式 |

启动时还会校验 `MAX_INTERVAL >= MIN_INTERVAL`，并把默认值夹到合法区间内，保证 `/api/info` 里公布的默认值一定是一个能成功的请求。

---

## 三种协议的实现细节

### HTTP Streaming

`fetch` + `ReadableStream` 读取 chunked 响应，自己按 `\n` 切帧。之所以不用 `EventSource`，是因为 NDJSON 需要对 chunk 边界有完全控制权，而且要在服务端 flush 的瞬间就拿到帧，而不是等整个响应结束。

每写一帧后调用 `ResponseController.Flush()`，并对这一帧设置写超时（`SetWriteDeadline`）。客户端断开时 `r.Context()` 被取消，写入返回错误，goroutine 立即退出。

### SSE

用原生 `EventSource`，监听具名事件 `data`。协议细节：连接建立后先写一个 `: stream open` 注释行，用来立刻触发 flush 并让反向代理放行缓冲。

**故意屏蔽了浏览器内置的自动重连**（在 `onerror` 里直接 `close()`）：重试策略由前端统一控制，这样三种协议行为一致，而且一个「时长跑完自然结束」的流不会被认为是掉线然后无限重连。

### WebSocket

`github.com/coder/websocket`。协议细节：

- 禁用压缩（`CompressionDisabled`），避免压缩引入额外延迟影响测量
- 不协商任何子协议，所以响应里 `Sec-WebSocket-Extensions` 为空
- 服务端用 `CloseRead` 起一个后台读取器，以便**立刻感知客户端断开**，而不是等下一次写入才发现
- 每帧写入都带独立的 `context.WithTimeout`
- 正常结束发送关闭码 `1000` + `running completed`；服务端关停发 `1001`；写失败发 `1011`
- 关闭时先走优雅关闭握手，2 秒内没完成就 `CloseNow()` 强制断开

一个容易踩的坑：WebSocket 升级后 `r.Context()` 就不再可用了，所以 WebSocket 处理器用 `context.Background()` 去申请流配额。

### 三者共用的部分

- **同一套帧格式**，所以三种协议的测量结果可以直接对比
- **绝对时间网格 pacing**：帧在 t=0、interval、2·interval… 发出，不做累积漂移补偿。如果某一帧因为调度抖动迟到了超过一个间隔，pacer 会**重新对齐**而不是连续补发（补发会造成假的突发，让测量结果失真）
- **统一的配额与关闭路径**：`Manager.Acquire` 在 WebSocket 升级**之前**执行，所以并发超限在三种协议上都表现为统一的 HTTP 503 JSON，而不是一个已经升级完成才被拒绝的连接

---

## 指标定义

### 字节数怎么算

「接收数据量」只统计**应用层字节**：

- HTTP 流：NDJSON 响应体本身的字节数（含行尾 `\n`）
- SSE：重建出来的事件字节数（`id:` + `event:` + `data:` 及其终止符）
- WebSocket：消息负载字节数

**IP、TCP、TLS、HTTP chunked 分帧、WebSocket 帧头都不计入。** 所以三种协议的字节数口径一致，可以横向比较。

### 中断阈值

```
阈值 = max(3 × interval, interval + 250ms)
```

取这个值是因为：间隔的 3 倍能过滤掉正常的调度抖动；`+250ms` 保证即使 interval 很小（比如 10ms），也不会把普通的毫秒级抖动误判成中断。

间隔必须在阈值**之上**才算中断。

### 几个容易混淆的指标

| 指标 | 定义 |
| --- | --- |
| `最大间隔` | 所有相邻帧间隔的最大值，**不筛阈值** |
| `最长流间隙` | 只有超阈值的间隙才计入 |
| `中断次数` | 超阈值间隙的个数 |
| `丢帧数` | 由 `sequence` 跳号推导，**只在同一条连接内统计** |
| `重连次数` | 仅开启自动重连且确实断过时非零 |

**重连期间的停机时间被记为一次重连，不计入流间隙。** 因为那是「连接不存在」，而不是「连接存在但数据没来」，混在一起会让数据没法解释。重连后序号从 1 重新开始，所以丢帧统计的基线也会重置。

运行结束时如果还有一个**未闭合的间隙**（比如流正好卡死时被停掉），它会被补记进统计——否则「卡死」这种情况反而不会体现在结果里。

---

## 项目结构

```
NSST/
├── cmd/server/main.go            # 启动、信号处理、优雅关闭
├── internal/
│   ├── api/                      # 路由、REST 处理器、CORS
│   ├── config/                   # 环境变量加载与校验
│   ├── httpstream/               # HTTP NDJSON 处理器
│   ├── httpx/                    # 错误信封、CORS、panic 恢复
│   ├── metrics/                  # 服务端原子计数器
│   ├── sse/                      # SSE 处理器
│   ├── stream/                   # 核心：pacing、生命周期、配额、结果分类
│   ├── testsupport/              # 测试用服务器脚手架
│   ├── websocket/                # WebSocket 处理器
│   └── webui/                    # go:embed 前端 + 静态文件服务
│       └── dist/                 # npm run build 的产物（已提交，见上文说明）
├── pkg/protocol/                 # 帧编解码（对外可复用）
├── web/
│   ├── index.html
│   ├── public/favicon.svg
│   ├── src/
│   │   ├── main.ts               # 三协议并发运行器 + 共享时钟
│   │   ├── views.ts              # 通俗卡片 + 六张表 + 每协议结论
│   │   ├── i18n.ts               # 中英文词典与语言切换
│   │   ├── metrics.ts            # 客户端指标采集
│   │   ├── chart.ts              # uPlot 多序列图表
│   │   ├── http-stream.ts        # NDJSON 客户端
│   │   ├── sse.ts                # EventSource 客户端
│   │   ├── websocket.ts          # WebSocket 客户端
│   │   ├── transport.ts          # 传输层公共抽象
│   │   ├── api.ts                # REST 调用
│   │   ├── ui.ts                 # DOM 辅助与事件日志
│   │   ├── format.ts             # 格式化
│   │   ├── types.ts              # 与后端共享的类型
│   │   └── styles.css
│   ├── package.json
│   ├── tsconfig.json
│   └── vite.config.ts
├── Dockerfile                    # 三阶段构建
├── docker-compose.yml
├── Makefile
├── go.mod / go.sum
└── LICENSE
```

---

## 测试

### 运行

```bash
go test ./...                  # 全量单测
go test -race -count=1 ./...   # 竞态检测（长连接必跑）
go test -coverprofile=coverage.out ./... && go tool cover -func=coverage.out
gofmt -l .                     # 应无输出
go vet ./...                   # 应无输出
```

或者用 Makefile：

```bash
make test          # go test ./...
make test-race     # 竞态检测
make lint          # gofmt 检查 + go vet
make smoke         # 对已运行的服务做三协议冒烟测试
```

### 覆盖情况

71 个测试函数，10 个测试文件：

| 包 | 数量 | 重点 |
| --- | --- | --- |
| `pkg/protocol` | 7 | 帧编解码、字节级比对、`payload_size=0` 的 3 字段形式、编码器缓存 |
| `internal/stream` | 25 | pacer 不漂移/不补发、参数边界、写错误分类、配额、排空、强制关闭 |
| `internal/api` | 15 | 全部端点、404/405、三协议非法参数、并发上限 503、CORS 四种配置 |
| `internal/httpstream` | 7 | 响应头、逐帧结构、首帧立刻到达、客户端断开、空负载 |
| `internal/sse` | 5 | 事件格式、开场注释、心跳、断开 |
| `internal/websocket` | 7 | 握手、帧序、时长、正常关闭、异常断开、并发上限、**goroutine 泄漏** |
| `internal/config` | 5 | 默认值、覆盖、非法值、夹取、空值 |

### 特别关注的几项

- **goroutine 泄漏**：WebSocket 测试跑 8 个会话后对比 goroutine 基线，容差 4
- **客户端断开**：三种协议都验证断开后服务端立即释放配额和 goroutine
- **优雅关闭**：验证收到信号后在途的流被排空，超时才强制关闭
- **竞态**：整仓 `-race` 干净

### 实测结果

在 Docker 容器内实测（`hyumerin/nsst`）：

| 检查项 | 结果 |
| --- | --- |
| `GET /api/health` | `{"status":"ok","version":"1.0.0"}` |
| HTTP 流 `duration=1&interval=250` | 正好 4 帧，`server_time` 间隔 250 ms |
| SSE `duration=1&interval=250` | `: stream open` + 正好 4 个 `data` 事件 |
| WebSocket 握手 | `HTTP/1.1 101 Switching Protocols`，无扩展协商 |
| WebSocket 关闭 | 关闭码 `1000`，原因 `stream completed` |
| 前端 `GET /` | 200，JS/CSS 资源 200 |
| 容器健康检查 | `Up (healthy)` |
| 优雅停止 | 退出码 `0`，日志 `draining` → `streams drained` → `shutdown complete` |
| 镜像体积 | 21.3 MB，非 root（uid 10001）运行 |

前端实测（50 帧 @100ms，三协议并发）：

| 协议 | 帧数 | 平均间隔 | 最大间隔 | 控制台错误 |
| --- | --- | --- | --- | --- |
| HTTP Streaming | 50 / 50 | 100.0 ms | 102 ms | 0 |
| SSE | 50 / 50 | 100.0 ms | 108 ms | 0 |
| WebSocket | 50 / 50 | 100.0 ms | 110 ms | 0 |

---

## 发布流程

打一个 `v*` 标签，剩下的事情由 `.github/workflows/release.yml` 自动完成。

```bash
git tag v1.0.0
git push origin v1.0.0
```

### 这个标签会触发什么

三个任务：

1. **Verify** — `gofmt` 检查、`go vet`、`go test -race`、前端类型检查。任何一项失败都会挡住后面的发布。
2. **Publish release** — 从**打标签时的源码**重新构建前端（不信任仓库里已提交的产物），交叉编译五个平台，生成 `checksums.txt`，然后创建 GitHub Release（自动生成更新说明）：
   - `nsst_1.0.0_linux_amd64` / `_linux_arm64`
   - `nsst_1.0.0_darwin_amd64` / `_darwin_arm64`
   - `nsst_1.0.0_windows_amd64.exe`
   - 每个二进制都通过 `-ldflags -X` 把版本号编进去，`/api/health` 会如实返回
3. **Publish container image** — 构建 `linux/amd64` + `linux/arm64` 双架构镜像并推送，标签为 `1.0.0`、`1.0`、`latest` 和原始标签名 `v1.0.0`。预发布标签（如 `v1.2.0-rc1`）**不会**移动 `latest`。

### 镜像推到哪里

| 目标 | 需要配置 | 说明 |
| --- | --- | --- |
| `ghcr.io/higashitaniyume/nsst` | 无 | 用内置的 `GITHUB_TOKEN`，开箱即用 |
| `hyumerin/nsst`（Docker Hub） | 两个 secret | 未配置时自动跳过，不影响 release |

要启用 Docker Hub，在仓库的 **Settings → Secrets and variables → Actions** 里加：

| Secret | 值 |
| --- | --- |
| `DOCKERHUB_USERNAME` | `hyumerin` |
| `DOCKERHUB_TOKEN` | 在 Docker Hub 生成的 Access Token，权限选 **Read & Write**（不要用登录密码） |

配好之后，同样的标签会自动多推一份到 Docker Hub，`docker pull hyumerin/nsst` 就能用了。

> 第一次推送到 GHCR 后，包默认是私有的。如果要公开，去 GitHub 的 **Packages** 页面把 `nsst` 的可见性改成 public。

### 演练

不想真的发布时，在 **Actions → Release → Run workflow** 手动触发一次。它会完整跑一遍校验和双架构镜像构建，但**不推送任何东西**、也**不创建 release**。

### 常见情况

- **流水线失败后重跑**：release 任务是幂等的，重跑会用 `--clobber` 覆盖已存在的附件，不会因为 release 已存在而失败。
- **想改 tag 重发**：先删掉 release 和 tag（`gh release delete v1.0.0 --yes --cleanup-tag`），再重新打。注意已经推到镜像仓库的同名标签不会被这个流程删除。

---

## 设计取舍

**为什么不用 `SetReadDeadline`/`WriteTimeout` 在 `http.Server` 上？**
因为那会杀掉所有长连接。写超时只能逐帧设置（`ResponseController.SetWriteDeadline`），`http.Server` 上只保留 `ReadHeaderTimeout` 和 `IdleTimeout`。

**为什么 pacer 迟到后重新对齐而不是补发？**
补发会造成一次人为突发，让吞吐和间隔的测量结果失真。真实系统里迟到就是迟到，如实记录比「努力追上」更有诊断价值。

**为什么客户端要屏蔽 `EventSource` 的自动重连？**
否则一个正常跑完的流会被当成掉线，无限重连下去，结果永远不收敛。重连必须是一个显式选择（界面上有开关，默认关闭）。

**为什么 WebSocket 的配额申请放在升级之前？**
如果在 `ws.Accept` 之后才申请，超限时连接已经升级完成，只能发一个关闭帧，客户端拿不到 HTTP 状态码，也就拿不到结构化的错误原因。放在前面，三种协议的过载表现完全一致。

**为什么重连不计入流间隙？**
「连接不存在」和「连接存在但数据没来」是两种不同的故障。混在一起统计，得到的数字无法解释。

**为什么前端不用框架？**
页面结构是固定的三张卡片 + 六张表 + 三张图，没有复杂的状态树和路由。原生 TS 让构建产物只有 ~108 KB，也没有框架升级的维护负担。

---

## 常见问题

**页面显示但数据不动 / 帧数一直是 0**
检查浏览器控制台和页面底部的事件日志。最常见的是并发流被限制（把 `MAX_CONCURRENT_STREAMS` 调大，它必须 ≥ 3 才能在自动启动时同时跑三种协议）。

**SSE 的帧成批到达，间隔看起来很大**
中间有反向代理在缓冲。本工具已经发送 `X-Accel-Buffering: no`，但某些代理还需要单独配置。这也是三种协议要同时测的原因——如果只有 SSE 有问题而 HTTP/WebSocket 正常，基本可以确定是代理或协议实现的问题。

**重启后参数没变**
参数存在内存里，不落盘。每次启动都用配置的默认值。

**改了前端代码但页面没变**
`go build` 不会重新打包前端。需要 `cd web && npm run build`，或者用 `npm run dev` 走 Vite 开发服务器。

**怎么确认服务端真的在推送而不是在缓冲**
看 `curl -N` 的输出是否逐帧出现。如果是等一会儿一次性全部打印出来，说明中间有缓冲。

---

## License

MIT，见 [LICENSE](LICENSE)。
