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
  'cfg.payload': 'Payload',
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
  'cfg.payload': '负载',
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
