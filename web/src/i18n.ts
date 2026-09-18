/**
 * Minimal, dependency-free i18n layer.
 *
 * Two locales are supported: Simplified Chinese (`zh`) and English (`en`).
 * The initial language comes from localStorage, then from the browser, and the
 * choice is remembered. Elements in the static markup declare their key with
 * `data-i18n`, `data-i18n-title` or `data-i18n-aria`; anything rendered from
 * TypeScript calls {@link t} directly.
 */

export type Lang = 'zh' | 'en';

export const LANGS: readonly Lang[] = ['zh', 'en'];

const STORAGE_KEY = 'nsst.lang';

type Dict = Record<string, string>;

const en: Dict = {
  'app.title': 'Network Stream Stability Tester',
  'app.subtitle': 'HTTP Streaming · SSE · WebSocket · running side by side',
  'lang.switch': '中文',
  'lang.switchTitle': 'Switch to Chinese',

  'server.connecting': 'connecting…',
  'server.online': 'online · v{version} · up {uptime}',
  'server.offline': 'offline',

  'run.running': 'Testing',
  'run.stopped': 'Stopped',
  'run.noteAuto': 'Started automatically with the server defaults, running until you stop it.',
  'run.noteManual': 'Manual run in progress.',
  'run.noteStopped': 'Test stopped. The tables below hold the final readings.',
  'run.stop': 'Stop all tests',

  'state.idle': 'Idle',
  'state.connecting': 'Connecting',
  'state.connected': 'Connected',
  'state.completed': 'Completed',
  'state.disconnected': 'Disconnected',
  'state.error': 'Error',

  'proto.http-stream': 'HTTP Streaming',
  'proto.sse': 'Server-Sent Events',
  'proto.websocket': 'WebSocket',

  'client.title': 'Client & connection',
  'client.hide': 'Hide',
  'client.hideTitle': 'Hide this panel; it comes back on the next visit',
  'client.show': 'Show',
  'client.showTitle': 'Show the client and connection details',
  'client.unavailable': 'The server did not answer /api/client, so only the browser-side facts are shown.',
  'client.note':
    'The address and the request headers are what this server saw on the request that loaded the page; a proxy header is only as trustworthy as the proxy that sets it. Everything else comes from the browser\u2019s own APIs and never leaves this machine.',
  'client.ip': 'IP address',
  'client.ipSource': 'Address source',
  'client.direct': 'direct connection',
  'client.viaHeader': '{header} header',
  'client.remoteAddr': 'Socket peer',
  'client.transport': 'Transport',
  'client.plain': 'plaintext',
  'client.host': 'Server host',
  'client.browser': 'Browser',
  'client.language': 'Language',
  'client.timezone': 'Time zone',
  'client.screen': 'Screen',
  'client.hardware': 'Device',
  'client.coresValue': '{count} cores',
  'client.memoryValue': '{value} GB RAM',
  'client.network': 'Network',
  'client.downlinkValue': '{value} Mbps',
  'client.rttValue': '{value} ms RTT',
  'client.clockSkew': 'Clock skew',
  'client.skewTitle':
    'Server clock minus browser clock. It is measured across one request, so it is accurate to about half a round trip.',
  'client.fullUserAgent': 'User agent',
  'client.page': 'Page URL',

  'export.all': 'Export all as CSV',
  'export.allTitle': 'Download one table with every protocol side by side',
  'export.protocol': 'Export',
  'export.protocolTitle': 'Download this protocol\u2019s tables as CSV',
  'export.column.group': 'Section',
  'export.column.metric': 'Metric',
  'export.group.meta': 'Export details',
  'export.group.verdict': 'Verdict',
  'export.verdict': 'Conclusion',
  'export.meta.app': 'Console',
  'export.meta.generatedAt': 'Exported at',
  'export.meta.parameters': 'Test parameters',
  'export.meta.expectedFrames': 'Expected frames',
  'export.meta.runState': 'Run state',
  'export.frameDetail.title': 'Frame-by-frame detail',
  'export.meta.frameDetail': 'Frame detail kept',
  'export.frame.protocol': 'Protocol',
  'export.frame.connection': 'Connection',
  'export.frame.sequence': 'Sequence',
  'export.frame.arrival': 'Arrival (ms)',
  'export.frame.interval': 'Interval (ms)',
  'export.frame.serverInterval': 'Server interval (ms)',
  'export.frame.serverTime': 'Server time (Unix ms)',
  'export.frame.payloadBytes': 'Payload bytes',
  'export.frame.wireBytes': 'Received bytes',
  'export.frame.missing': 'Missing frames',
  'export.frame.integrity': 'Integrity',
  'export.frame.stall': 'Interruption',
  'export.ok': 'ok',
  'export.failed': 'failed',
  'export.yes': 'yes',
  'export.no': 'no',

  'overview.title': 'Frame interval, all three protocols',

  'table.traffic': 'Connection & traffic',
  'table.timing': 'Timing & gaps',

  'row.status': 'Status',
  'row.frames': 'Frames received',
  'row.bytes': 'Data received',
  'row.tpAvg': 'Average throughput',
  'row.tpNow': 'Current throughput',
  'row.firstFrame': 'First frame latency',
  'row.integrity': 'Frame integrity',
  'row.target': 'Target interval',
  'row.intervalAvg': 'Average interval',
  'row.intervalMin': 'Minimum interval',
  'row.intervalMax': 'Maximum interval',
  'row.longestGap': 'Longest stream gap',
  'row.interruptions': 'Interruptions',
  'row.missing': 'Missing frames',
  'row.reconnects': 'Reconnects',

  'value.na': '—',
  'value.none': 'none',
  'value.allVerified': 'all frames verified',
  'value.failedVerification': '{count} frame(s) failed verification',
  'value.bounded': '{actual} / {expected}',

  'chart.interval': 'Frame interval',
  'chart.intervalHint':
    'Time between consecutive frame arrivals, in milliseconds. The last minute, scrolling.',
  'chart.throughput': 'Throughput',
  'chart.throughputHint': 'Application bytes received per second. The last minute, scrolling.',
  'chart.peaks': 'Interruption peaks',
  'chart.peaksHint': 'Every gap that exceeded the threshold, plotted at the moment it ended.',

  'cfg.title': 'Test parameters',
  'cfg.locked': 'A test is running. Stop it to change the parameters and start a new run.',
  'cfg.ready': 'The first run used the server defaults. Adjust anything below, then start a new run.',
  'cfg.protocols': 'Protocols',
  'cfg.duration': 'Duration',
  'cfg.interval': 'Interval',
  'cfg.payload': 'Text per frame',
  'cfg.custom': 'Custom',
  'cfg.untilStopped': 'Run until stopped',
  'cfg.untilStoppedHint': 'Ignore the duration above and keep streaming until you press stop.',
  'cfg.autoReconnect': 'Auto-reconnect on drop',
  'cfg.autoReconnectHint': 'Re-establish a dropped connection instead of reporting it as an interruption.',
  'cfg.start': 'Start test',
  'cfg.stop': 'Stop test',

  'unit.seconds': 's',
  'unit.ms': 'ms',
  'unit.minutes': 'min',
  'unit.bytes': 'B',
  'unit.untilStopped': 'until stopped',

  'live.title': 'Live data',
  'live.hint':
    'The bytes the server actually sent, appended in the order they arrived. When the text stops moving the stream has stalled. Only the most recent stretch is kept.',
  'live.idle': 'waiting for the first frame…',

  'log.title': 'Event log',
  'log.clear': 'Clear',
  'log.separator': '────────────────────────────',
  'log.connected': 'Connected to {name} v{version} · protocols: {protocols}',
  'log.limits':
    'Server limits — duration ≤ {duration} · interval {minInterval}–{maxInterval} ms · payload ≤ {payload} · concurrent streams ≤ {streams} · write timeout {writeTimeout} ms',
  'log.autoStarted': 'Auto-started {protocols} with the server defaults ({interval} ms, {payload} payload), running until stopped',
  'log.started': 'Started {protocols} — duration {duration}, interval {interval}, payload {payload}',
  'log.stopped': 'Test stopped by the user',
  'log.completed': '{protocol} completed: {detail}',
  'log.earlyEnd': '{protocol} ended early: {detail}',
  'log.interruption': '{protocol} stream interruption: {gap} between frame {from} and frame {to}',
  'log.sequenceGap': '{protocol} skipped {count} frame(s) at sequence {sequence}',
  'log.reconnecting': '{protocol}: reconnecting in {delay} ms',
  'log.reconnected': '{protocol}: connection re-established',
  'log.retryGaveUp': '{protocol}: giving up after {count} reconnect attempts',
  'log.configError': 'Could not read /api/config: {detail}',
  'log.late': '{protocol}: the server did not close the stream in time; ended locally',
  'log.integrity': '{protocol}: {count} frame(s) failed payload verification',
  'log.exported': 'Exported {protocols} to {file}',

  'close.completed': 'the server closed the stream',
  'err.network': 'network error: {detail}',
  'err.httpStatus': 'HTTP {status}: {detail}',
  'err.noBody': 'the response carries no readable body',
  'err.malformedNDJSON': 'malformed NDJSON frame: {detail}',
  'err.noSequence': 'frame without a sequence number: {detail}',
  'err.truncated': 'the stream ended with a truncated frame: {detail}',
  'err.bodyEnded': 'the response body ended mid-stream',
  'err.malformedSSE': 'malformed SSE payload: {detail}',
  'err.sseNotEstablished': 'the event stream could not be established (the server rejected the request)',
  'err.sseClosed': 'the event stream was closed by the server',
  'err.sseEnded': 'the event stream ended',
  'err.binaryFrame': 'received a binary WebSocket frame; the stream is text only',
  'err.malformedFrame': 'malformed WebSocket frame: {detail}',
  'err.wsHandshake': 'the WebSocket handshake failed (close code {code})',
  'err.wsClosed': 'the server closed the connection with code {code}{detail}',
  'err.wsLost': 'the connection was lost without a close handshake (close code {code})',

  'verdict.stable': 'Stable: {frames} frames arrived on schedule, no gap exceeded {threshold}.',
  'verdict.noGapYet': 'No gap above {threshold} so far, {frames} frames received.',
  'verdict.noFrames': 'No frame received yet.',
  'verdict.interrupted':
    '{count} interruption(s); the longest gap was {gap} against a target interval of {interval}.',
  'verdict.endedEarly': 'The stream ended before the requested duration ({frames} of {expected} frames).',
  'verdict.untilStopped': '{frames} frames over {elapsed}; no gap exceeded {threshold}.',

  'about.title': 'How to read these numbers',
  'about.threshold':
    'An interruption is a gap between two frame arrivals of at least three times the requested interval, and never below interval + 250 ms. Each protocol card shows the gap it measured against that threshold.',
  'about.gap':
    'An interruption is an application level observation and is deliberately not called packet loss. A browser cannot see TCP retransmissions, so a stable connection with a stalled producer also produces interruptions.',
  'about.bytes':
    'Data received counts application bytes only: the exact NDJSON response body, the exact SSE event bytes, or the WebSocket message payload. IP, TCP, TLS and framing overhead are excluded.',
  'about.missing':
    'Missing frames is derived from the frame sequence numbers and only counts within a single connection; numbering restarts at 1 after a reconnect.',
  'about.reconnects':
    'Reconnects is only non-zero when auto-reconnect is enabled and a connection actually dropped before the run ended.',
  'resources.title': 'Server limits',
};

const zh: Dict = {
  'app.title': '网络流稳定性测试器',
  'app.subtitle': 'HTTP 流 · SSE · WebSocket 三路同时测试',
  'lang.switch': 'EN',
  'lang.switchTitle': '切换到英文',

  'server.connecting': '连接中…',
  'server.online': '在线 · v{version} · 已运行 {uptime}',
  'server.offline': '离线',

  'run.running': '测试进行中',
  'run.stopped': '已停止',
  'run.noteAuto': '已用服务端默认参数自动开始，会一直运行直到你点「停止」。',
  'run.noteManual': '手动测试进行中。',
  'run.noteStopped': '测试已停止，下方表格保留的是最终读数。',
  'run.stop': '停止全部测试',

  'state.idle': '空闲',
  'state.connecting': '连接中',
  'state.connected': '已连接',
  'state.completed': '已完成',
  'state.disconnected': '已断开',
  'state.error': '错误',

  'proto.http-stream': 'HTTP 流',
  'proto.sse': 'SSE',
  'proto.websocket': 'WebSocket',

  'client.title': '客户端与连接',
  'client.hide': '隐藏',
  'client.hideTitle': '隐藏这块面板，下次访问时恢复显示',
  'client.show': '显示',
  'client.showTitle': '显示客户端与连接信息',
  'client.unavailable': '服务端没有响应 /api/client，这里只显示浏览器自身能看到的信息。',
  'client.note':
    'IP 和请求头是服务端处理这次页面请求时看到的值；代理请求头只有在前置代理确实会设置它时才可信。其余字段来自浏览器自己的 API，不会离开这台机器。',
  'client.ip': 'IP 地址',
  'client.ipSource': '地址来源',
  'client.direct': '直连（连接对端地址）',
  'client.viaHeader': '{header} 请求头',
  'client.remoteAddr': '连接对端',
  'client.transport': '传输',
  'client.plain': '明文',
  'client.host': '服务端主机',
  'client.browser': '浏览器',
  'client.language': '语言',
  'client.timezone': '时区',
  'client.screen': '屏幕',
  'client.hardware': '设备',
  'client.coresValue': '{count} 核',
  'client.memoryValue': '{value} GB 内存',
  'client.network': '网络',
  'client.downlinkValue': '{value} Mbps',
  'client.rttValue': '往返 {value} 毫秒',
  'client.clockSkew': '时钟偏差',
  'client.skewTitle': '服务端时钟减去本机时钟。跨一次请求测得，误差约为往返时间的一半。',
  'client.fullUserAgent': 'User-Agent',
  'client.page': '页面地址',

  'export.all': '导出总表格',
  'export.allTitle': '把各协议并排导出成一张 CSV 表',
  'export.protocol': '导出',
  'export.protocolTitle': '把这个协议的两张表导出为 CSV',
  'export.column.group': '分组',
  'export.column.metric': '指标',
  'export.group.meta': '导出信息',
  'export.group.verdict': '判定',
  'export.verdict': '结论',
  'export.meta.app': '控制台',
  'export.meta.generatedAt': '导出时间',
  'export.meta.parameters': '测试参数',
  'export.meta.expectedFrames': '预期帧数',
  'export.meta.runState': '运行状态',
  'export.frameDetail.title': '逐帧明细',
  'export.meta.frameDetail': '逐帧明细保留',
  'export.frame.protocol': '协议',
  'export.frame.connection': '连接',
  'export.frame.sequence': '序号',
  'export.frame.arrival': '到达时刻（毫秒）',
  'export.frame.interval': '帧间隔（毫秒）',
  'export.frame.serverInterval': '服务端间隔（毫秒）',
  'export.frame.serverTime': '服务端时间（Unix 毫秒）',
  'export.frame.payloadBytes': '负载字节',
  'export.frame.wireBytes': '接收字节',
  'export.frame.missing': '丢帧',
  'export.frame.integrity': '帧完整',
  'export.frame.stall': '中断',
  'export.ok': '通过',
  'export.failed': '失败',
  'export.yes': '是',
  'export.no': '否',

  'overview.title': '三种协议帧间隔对比',

  'table.traffic': '连接与流量',
  'table.timing': '时延与间隔',

  'row.status': '状态',
  'row.frames': '收到帧数',
  'row.bytes': '接收数据量',
  'row.tpAvg': '平均吞吐',
  'row.tpNow': '当前吞吐',
  'row.firstFrame': '首帧延迟',
  'row.integrity': '帧完整性',
  'row.target': '目标间隔',
  'row.intervalAvg': '平均间隔',
  'row.intervalMin': '最小间隔',
  'row.intervalMax': '最大间隔',
  'row.longestGap': '最长流间隙',
  'row.interruptions': '中断次数',
  'row.missing': '丢帧数',
  'row.reconnects': '重连次数',

  'value.na': '—',
  'value.none': '未出现',
  'value.allVerified': '全部校验通过',
  'value.failedVerification': '{count} 帧校验失败',
  'value.bounded': '{actual} / {expected}',

  'chart.interval': '帧间隔',
  'chart.intervalHint': '相邻两帧的到达间隔，单位毫秒。只显示最近 1 分钟，随时间滚动。',
  'chart.throughput': '吞吐',
  'chart.throughputHint': '每秒接收到的应用层字节数。只显示最近 1 分钟，随时间滚动。',
  'chart.peaks': '中断点',
  'chart.peaksHint': '每次超过阈值的间隙，按它结束的时刻打点。',

  'cfg.title': '测试参数',
  'cfg.locked': '测试进行中，停止后才能修改参数并开始新一轮测试。',
  'cfg.ready': '第一轮用的是服务端默认参数。改完下面的项目后可以开始新一轮测试。',
  'cfg.protocols': '协议',
  'cfg.duration': '时长',
  'cfg.interval': '间隔',
  'cfg.payload': '每帧文本',
  'cfg.custom': '自定义',
  'cfg.untilStopped': '运行到手动停止',
  'cfg.untilStoppedHint': '忽略上面的时长，一直推流直到你点停止。',
  'cfg.autoReconnect': '断开后自动重连',
  'cfg.autoReconnectHint': '断线后自动重建连接，而不是把它记为一次中断。',
  'cfg.start': '开始测试',
  'cfg.stop': '停止测试',

  'unit.seconds': '秒',
  'unit.ms': '毫秒',
  'unit.minutes': '分钟',
  'unit.bytes': '字节',
  'unit.untilStopped': '直到手动停止',

  'live.title': '实时数据',
  'live.hint':
    '服务端真正发过来的字节，按到达顺序追加。文本不动了就说明这条流卡住了。每个协议只保留最近一段。',
  'live.idle': '等待第一帧…',

  'log.title': '事件日志',
  'log.clear': '清空',
  'log.separator': '────────────────────────────',
  'log.connected': '已连接 {name} v{version} · 协议：{protocols}',
  'log.limits':
    '服务端限制 —— 时长 ≤ {duration} · 间隔 {minInterval}–{maxInterval} 毫秒 · 负载 ≤ {payload} · 并发流 ≤ {streams} · 写超时 {writeTimeout} 毫秒',
  'log.autoStarted': '已用服务端默认参数自动开始 {protocols}（间隔 {interval} 毫秒，负载 {payload}），直到手动停止',
  'log.started': '已开始 {protocols} —— 时长 {duration}，间隔 {interval}，负载 {payload}',
  'log.stopped': '用户停止了测试',
  'log.completed': '{protocol} 已完成：{detail}',
  'log.earlyEnd': '{protocol} 提前结束：{detail}',
  'log.interruption': '{protocol} 出现流中断：第 {from} 帧与第 {to} 帧之间间隔 {gap}',
  'log.sequenceGap': '{protocol} 在序号 {sequence} 处跳过了 {count} 帧',
  'log.reconnecting': '{protocol}：{delay} 毫秒后重连',
  'log.reconnected': '{protocol}：连接已重建',
  'log.retryGaveUp': '{protocol}：重连 {count} 次后放弃',
  'log.configError': '读取 /api/config 失败：{detail}',
  'log.late': '{protocol}：服务端没有按时关闭流，已在本地结束',
  'log.integrity': '{protocol}：{count} 帧负载校验失败',
  'log.exported': '已导出 {protocols} 到 {file}',

  'close.completed': '服务端关闭了流',
  'err.network': '网络错误：{detail}',
  'err.httpStatus': 'HTTP {status}：{detail}',
  'err.noBody': '响应没有可读的响应体',
  'err.malformedNDJSON': 'NDJSON 帧格式错误：{detail}',
  'err.noSequence': '帧缺少 sequence 字段：{detail}',
  'err.truncated': '流结束时最后一帧被截断：{detail}',
  'err.bodyEnded': '响应体在流中途结束',
  'err.malformedSSE': 'SSE 数据格式错误：{detail}',
  'err.sseNotEstablished': '无法建立事件流（服务端拒绝了请求）',
  'err.sseClosed': '事件流被服务端关闭',
  'err.sseEnded': '事件流已结束',
  'err.binaryFrame': '收到二进制 WebSocket 帧，本流只发送文本',
  'err.malformedFrame': 'WebSocket 帧格式错误：{detail}',
  'err.wsHandshake': 'WebSocket 握手失败（关闭码 {code}）',
  'err.wsClosed': '服务端关闭了连接，关闭码 {code}{detail}',
  'err.wsLost': '连接在没有关闭握手的情况下丢失（关闭码 {code}）',

  'verdict.stable': '稳定：{frames} 帧全部按计划到达，没有超过 {threshold} 的流间隙。',
  'verdict.noGapYet': '目前没有超过 {threshold} 的间隙，已收到 {frames} 帧。',
  'verdict.noFrames': '还没有收到任何帧。',
  'verdict.interrupted': '出现 {count} 次中断；最长间隙 {gap}，而目标间隔是 {interval}。',
  'verdict.endedEarly': '流在达到设定时长前就结束了（{frames} / {expected} 帧）。',
  'verdict.untilStopped': '{elapsed} 内收到 {frames} 帧，没有超过 {threshold} 的间隙。',

  'about.title': '指标说明',
  'about.threshold':
    '「中断」指两帧到达间隔达到目标间隔的三倍以上，且不低于 间隔 + 250 毫秒。每张协议表里都会给出它实际测到的间隔，方便和这个阈值对照。',
  'about.gap':
    '中断是应用层的观测结果，刻意不叫「丢包」。浏览器看不到 TCP 重传，所以即使网络稳定、只是服务端生产变慢，也会表现为中断。',
  'about.bytes':
    '「接收数据量」只统计应用层字节：HTTP 流是 NDJSON 响应体本身，SSE 是事件字节，WebSocket 是消息负载。IP、TCP、TLS 和分帧开销都不计入。',
  'about.missing':
    '「丢帧数」由帧里的 sequence 推导，只在同一条连接内统计；重连后序号会从 1 重新开始。',
  'about.reconnects': '只有在开启自动重连、且连接确实在测试结束前断开过，「重连次数」才不为 0。',
  'resources.title': '服务端限制',
};

const dictionaries: Record<Lang, Dict> = { en, zh };

function detectInitialLang(): Lang {
  try {
    const saved = window.localStorage.getItem(STORAGE_KEY);
    if (saved === 'zh' || saved === 'en') return saved;
  } catch {
    // Private mode or a blocked storage API: fall through to the browser locale.
  }
  return (navigator.language ?? '').toLowerCase().startsWith('zh') ? 'zh' : 'en';
}

let current: Lang = detectInitialLang();
const listeners = new Set<(lang: Lang) => void>();

export function getLang(): Lang {
  return current;
}

export function onLangChange(listener: (lang: Lang) => void): void {
  listeners.add(listener);
}

export function setLang(lang: Lang): void {
  if (lang === current) return;
  current = lang;
  try {
    window.localStorage.setItem(STORAGE_KEY, lang);
  } catch {
    // Persisting is best effort only.
  }
  applyStatic();
  for (const listener of listeners) listener(lang);
}

export function toggleLang(): void {
  setLang(current === 'zh' ? 'en' : 'zh');
}

/** Translates a key, substituting `{name}` placeholders. */
export function t(key: string, params?: Record<string, string | number>): string {
  const dict = dictionaries[current];
  let text = dict[key];
  if (text === undefined) text = dictionaries.en[key];
  if (text === undefined) return key;
  if (!params) return text;
  for (const name of Object.keys(params)) {
    text = text.split(`{${name}}`).join(String(params[name]));
  }
  return text;
}

/**
 * Fills in every element that declares a translation key. Called on start-up and
 * again whenever the language changes.
 */
export function applyStatic(root: ParentNode = document): void {
  for (const el of root.querySelectorAll<HTMLElement>('[data-i18n]')) {
    el.textContent = t(el.dataset.i18n ?? '');
  }
  for (const el of root.querySelectorAll<HTMLElement>('[data-i18n-title]')) {
    el.title = t(el.dataset.i18nTitle ?? '');
  }
  for (const el of root.querySelectorAll<HTMLElement>('[data-i18n-aria]')) {
    el.setAttribute('aria-label', t(el.dataset.i18nAria ?? ''));
  }
  document.documentElement.lang = current === 'zh' ? 'zh-CN' : 'en';
}
