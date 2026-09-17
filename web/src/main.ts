import { fetchConfig, fetchHealth, httpStreamURL, websocketURL } from './api.js';
import { MultiChart } from './chart.js';
import { formatAxisMs, formatAxisRate } from './format.js';
import { createHTTPStreamTransport } from './http-stream.js';
import { applyStatic, onLangChange, t, toggleLang } from './i18n.js';
import { MetricsCollector } from './metrics.js';
import { createSSETransport } from './sse.js';
import type { CloseReason, Message, Transport, TransportCallbacks } from './transport.js';
import type { ConfigResponse, ConnectionState, Protocol, StreamParams } from './types.js';
import { PROTOCOLS } from './types.js';
import { EventLog, NumericChoiceGroup, requireElement, setText } from './ui.js';
import { PROTOCOL_COLORS, ProtocolCard } from './views.js';
import { createWebSocketTransport } from './websocket.js';

import 'uplot/dist/uPlot.min.css';

/** How often the tables and the charts are refreshed. */
const TICK_MS = 200;
/** Delay before a dropped connection is re-established, when that is enabled. */
const RECONNECT_DELAY_MS = 1000;
/** Upper bound on automatic reconnects for a single run. */
const MAX_RECONNECTS = 10;
/** Grace period after the requested duration before the client stops waiting. */
const CLOSE_LATE_MS = 2000;

interface Sample {
  intervalMs: number;
  throughputBps: number;
  /** Size of an interruption that ended during this tick, or 0. */
  stallMs: number;
}

/**
 * One protocol's test run.
 *
 * Everything it reports comes from frames the transport actually delivered; the
 * class never invents a measurement. When a connection ends before the requested
 * duration it says so rather than silently restarting, unless auto-reconnect was
 * explicitly enabled.
 */
class StreamRun {
  readonly metrics: MetricsCollector;
  readonly protocol: Protocol;
  readonly card: ProtocolCard;

  private readonly log: EventLog;
  private readonly params: StreamParams;
  private readonly autoReconnect: boolean;
  private readonly deadlineMs: number;
  private readonly onSettled: () => void;

  private transport: Transport | null = null;
  private deadlineTimer: number | null = null;
  private reconnectTimer: number | null = null;
  private reconnects = 0;
  private finished = false;
  private pendingStallMs = 0;

  constructor(options: {
    protocol: Protocol;
    params: StreamParams;
    card: ProtocolCard;
    log: EventLog;
    autoReconnect: boolean;
    deadlineMs: number;
    onSettled: () => void;
  }) {
    this.protocol = options.protocol;
    this.params = options.params;
    this.card = options.card;
    this.log = options.log;
    this.autoReconnect = options.autoReconnect;
    this.deadlineMs = options.deadlineMs;
    this.onSettled = options.onSettled;
    this.metrics = new MetricsCollector(options.params.interval);
  }

  get isFinished(): boolean {
    return this.finished;
  }

  private get label(): string {
    return t(`proto.${this.protocol}`);
  }

  private readonly callbacks: TransportCallbacks = {
    onOpen: () => {
      if (this.finished) return;
      this.metrics.setState('connected');
      if (this.reconnects > 0) {
        this.log.add('info', t('log.reconnected', { protocol: this.label }));
      }
    },
    onFrame: (frame, wireBytes) => {
      if (this.finished) return;
      const observation = this.metrics.onFrame(frame, wireBytes);
      if (observation.stall && observation.intervalMs !== null) {
        if (observation.intervalMs > this.pendingStallMs) {
          this.pendingStallMs = observation.intervalMs;
        }
        this.log.add(
          'warn',
          t('log.interruption', {
            protocol: this.label,
            gap: `${observation.intervalMs.toFixed(0)} ms`,
            from: Math.max(0, frame.sequence - 1),
            to: frame.sequence,
          }),
        );
      }
    },
    onClose: (reason, message) => this.handleClose(reason, message),
    onError: (message) => this.reportProblem(message),
  };

  start(): void {
    this.metrics.start();
    this.openTransport();

    if (this.deadlineMs > 0) {
      // Safety net: the server promises to close the stream by the deadline, so
      // if it has not, the run ends locally instead of hanging forever.
      const waitMs = Math.max(0, this.deadlineMs - performance.now()) + CLOSE_LATE_MS;
      this.deadlineTimer = window.setTimeout(() => this.onDeadlinePassed(), waitMs);
    }
  }

  /** Ends the run because the user asked for it. */
  stop(): void {
    if (this.finished) return;
    this.finished = true;
    this.metrics.closeOpenGap();
    this.teardown();
    this.metrics.setState('disconnected', t('log.stopped'));
    this.onSettled();
  }

  /**
   * Produces one chart point, or `null` once the run has settled so that the
   * series simply stops instead of drawing a gap that never happened.
   */
  sample(now: number): Sample | null {
    if (this.finished) return null;
    const tick = this.metrics.tick(now);
    const stallMs = this.pendingStallMs;
    this.pendingStallMs = 0;
    return { intervalMs: tick.intervalMs, throughputBps: tick.throughputBps, stallMs };
  }

  snapshot(now: number) {
    return this.metrics.snapshot(now);
  }

  private openTransport(): void {
    this.transport = this.createTransport();
    this.transport.start();
  }

  private createTransport(): Transport {
    switch (this.protocol) {
      case 'http-stream':
        return createHTTPStreamTransport(httpStreamURL('http-stream', this.params), this.callbacks);
      case 'sse':
        return createSSETransport(httpStreamURL('sse', this.params), this.callbacks);
      case 'websocket':
        return createWebSocketTransport(websocketURL(this.params), this.callbacks);
    }
  }

  private handleClose(reason: CloseReason, message: Message): void {
    if (this.finished) return;
    const detail = t(message.key, message.params);
    const early = this.deadlineMs > 0 && performance.now() < this.deadlineMs - 50;

    if (reason === 'completed' && !early) {
      this.settle('completed', detail);
      return;
    }

    if (this.autoReconnect && this.reconnects < MAX_RECONNECTS) {
      this.reconnects += 1;
      this.metrics.noteReconnect();
      this.metrics.setState('connecting', detail);
      this.log.add('warn', t('log.reconnecting', { protocol: this.label, delay: RECONNECT_DELAY_MS }));
      this.reconnectTimer = window.setTimeout(() => {
        this.reconnectTimer = null;
        if (this.finished) return;
        this.transport = null;
        this.openTransport();
      }, RECONNECT_DELAY_MS);
      return;
    }

    if (this.autoReconnect) {
      this.log.add('error', t('log.retryGaveUp', { protocol: this.label, count: this.reconnects }));
    }
    this.settle('disconnected', detail);
  }

  private onDeadlinePassed(): void {
    this.deadlineTimer = null;
    if (this.finished) return;
    this.log.add('warn', t('log.late', { protocol: this.label }));
    this.settle('completed', t('close.completed'));
  }

  private reportProblem(message: Message): void {
    if (this.finished) return;
    this.log.add('error', t(message.key, message.params));
  }

  private settle(state: ConnectionState, detail: string): void {
    if (this.finished) return;
    this.finished = true;
    this.metrics.closeOpenGap();
    this.teardown();
    this.metrics.setState(state, detail);

    const snapshot = this.metrics.snapshot();
    const key = state === 'completed' ? 'log.completed' : 'log.earlyEnd';
    this.log.add(
      state === 'completed' ? 'success' : 'warn',
      t(key, {
        protocol: this.label,
        detail: `${snapshot.frames} frames, ${(snapshot.elapsedMs / 1000).toFixed(2)} s, ${detail}`,
      }),
    );
    if (snapshot.integrityErrors > 0) {
      this.log.add('error', t('log.integrity', { protocol: this.label, count: snapshot.integrityErrors }));
    }
    if (snapshot.missingFrames > 0) {
      this.log.add(
        'warn',
        t('log.sequenceGap', {
          protocol: this.label,
          count: snapshot.missingFrames,
          sequence: snapshot.frames,
        }),
      );
    }
    this.onSettled();
  }

  private teardown(): void {
    if (this.deadlineTimer !== null) {
      window.clearTimeout(this.deadlineTimer);
      this.deadlineTimer = null;
    }
    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    const transport = this.transport;
    this.transport = null;
    if (transport) transport.stop();
  }
}

/**
 * Runs every enabled protocol side by side and feeds the shared charts.
 *
 * The three streams share one ticker so their samples line up on the same x
 * axis, which is what makes the comparison charts readable.
 */
class Suite {
  private readonly cards = new Map<Protocol, ProtocolCard>();
  private readonly runs = new Map<Protocol, StreamRun>();
  private readonly intervalChart: MultiChart;
  private readonly throughputChart: MultiChart;
  private readonly peaksChart: MultiChart;

  private readonly log: EventLog;
  private readonly onRunningChanged: (running: boolean) => void;

  private ticker: number | null = null;
  private startedAt = 0;
  private params: StreamParams = { duration: 60, interval: 100, payload_size: 4096 };
  private expectedFrames = 0;
  private untilStopped = true;
  private running = false;

  constructor(options: {
    grid: HTMLElement;
    intervalChart: MultiChart;
    throughputChart: MultiChart;
    peaksChart: MultiChart;
    log: EventLog;
    onRunningChanged: (running: boolean) => void;
  }) {
    this.intervalChart = options.intervalChart;
    this.throughputChart = options.throughputChart;
    this.peaksChart = options.peaksChart;
    this.log = options.log;
    this.onRunningChanged = options.onRunningChanged;

    for (const protocol of PROTOCOLS) {
      const card = new ProtocolCard(protocol);
      this.cards.set(protocol, card);
      options.grid.append(card.root);
    }
  }

  get isRunning(): boolean {
    return this.running;
  }

  /** Re-applies translated labels on the cards and the chart legends. */
  renderLabels(): void {
    for (const [, card] of this.cards) card.renderLabels();
    const labels = PROTOCOLS.map((protocol) => t(`proto.${protocol}`));
    this.intervalChart.setSeriesLabels(labels);
    this.throughputChart.setSeriesLabels(labels);
    this.peaksChart.setSeriesLabels(labels);
  }

  /**
   * Starts a run for every protocol given.
   *
   * @param untilStopped ignore the duration and stream until the user stops
   */
  start(
    protocols: readonly Protocol[],
    params: StreamParams,
    untilStopped: boolean,
    autoReconnect: boolean,
  ): void {
    this.stop();
    this.params = params;
    this.untilStopped = untilStopped;
    this.expectedFrames = untilStopped
      ? 0
      : Math.ceil((params.duration * 1000) / params.interval);

    this.intervalChart.clear();
    this.throughputChart.clear();
    this.peaksChart.clear();
    for (const [, card] of this.cards) card.reset();

    this.runs.clear();
    this.startedAt = performance.now();
    this.running = true;

    for (const protocol of protocols) {
      const card = this.cards.get(protocol);
      if (!card) continue;
      const run = new StreamRun({
        protocol,
        params,
        card,
        log: this.log,
        autoReconnect,
        deadlineMs: untilStopped ? 0 : this.startedAt + params.duration * 1000,
        onSettled: () => this.handleRunSettled(),
      });
      this.runs.set(protocol, run);
      run.start();
    }

    this.ticker = window.setInterval(() => this.tick(), TICK_MS);
    this.onRunningChanged(true);
  }

  /** Stops every live run and freezes the tables at their last reading. */
  stop(): void {
    if (this.ticker !== null) {
      window.clearInterval(this.ticker);
      this.ticker = null;
    }
    for (const [, run] of this.runs) run.stop();
    const wasRunning = this.running;
    this.running = false;
    if (wasRunning) this.onRunningChanged(false);
  }

  private handleRunSettled(): void {
    if (!this.running) return;
    for (const [, run] of this.runs) {
      if (!run.isFinished) return;
    }
    // Every stream ended on its own: stop the ticker but keep the readings.
    if (this.ticker !== null) {
      window.clearInterval(this.ticker);
      this.ticker = null;
    }
    this.running = false;
    this.onRunningChanged(false);
  }

  private tick(): void {
    const now = performance.now();
    const x = (now - this.startedAt) / 1000;

    const intervals: (number | null)[] = [];
    const throughputs: (number | null)[] = [];
    const peaks: (number | null)[] = [];

    for (const protocol of PROTOCOLS) {
      const run = this.runs.get(protocol);
      const sample = run ? run.sample(now) : null;

      if (!run || !sample) {
        intervals.push(null);
        throughputs.push(null);
        peaks.push(null);
        continue;
      }

      intervals.push(sample.intervalMs);
      throughputs.push(sample.throughputBps);
      peaks.push(sample.stallMs > 0 ? sample.stallMs : null);
      run.card.update(
        run.snapshot(now),
        this.params,
        this.expectedFrames,
        this.untilStopped,
        this.running,
      );
    }

    this.intervalChart.push(x, intervals);
    this.throughputChart.push(x, throughputs);
    if (peaks.some((value) => value !== null)) {
      this.peaksChart.push(x, peaks);
    }
  }
}

// --------------------------------------------------------------------- boot

function seriesSpecs() {
  return PROTOCOLS.map((protocol) => ({
    label: t(`proto.${protocol}`),
    stroke: PROTOCOL_COLORS[protocol],
  }));
}

function peakSpecs() {
  return PROTOCOLS.map((protocol) => ({
    label: t(`proto.${protocol}`),
    stroke: PROTOCOL_COLORS[protocol],
    asPoints: true,
  }));
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function boot(): Promise<void> {
  applyStatic();

  const log = new EventLog(requireElement('log-list'));

  const intervalChart = new MultiChart(requireElement('chart-interval'), {
    series: seriesSpecs(),
    format: formatAxisMs,
    height: 200,
  });
  const throughputChart = new MultiChart(requireElement('chart-throughput'), {
    series: seriesSpecs(),
    format: formatAxisRate,
    height: 200,
  });
  const peaksChart = new MultiChart(requireElement('chart-peaks'), {
    series: peakSpecs(),
    format: formatAxisMs,
    height: 180,
  });

  const startButton = requireElement<HTMLButtonElement>('start-button');
  const stopButton = requireElement<HTMLButtonElement>('stop-button');
  const runState = requireElement('run-state');
  const runNote = requireElement('run-note');
  const cfgNotice = requireElement('cfg-notice');
  const serverChip = requireElement('server-chip');
  const untilStoppedInput = requireElement<HTMLInputElement>('cfg-until-stopped');
  const autoReconnectInput = requireElement<HTMLInputElement>('cfg-auto-reconnect');
  const protocolInputs = Array.from(
    requireElement('cfg-protocols').querySelectorAll<HTMLInputElement>('input[type=checkbox]'),
  );

  // --- parameter controls -------------------------------------------------
  const durationGroup = new NumericChoiceGroup(requireElement('cfg-duration'), {
    min: 1,
    max: 3600,
    onChange: () => undefined,
  });
  const intervalGroup = new NumericChoiceGroup(requireElement('cfg-interval'), {
    min: 10,
    max: 60000,
    step: 10,
    onChange: () => undefined,
  });
  const payloadGroup = new NumericChoiceGroup(requireElement('cfg-payload'), {
    min: 0,
    max: 1048576,
    onChange: () => undefined,
  });

  // Sensible values before /api/config answers, so the panel is never blank.
  durationGroup.set(60);
  intervalGroup.set(100);
  payloadGroup.set(4096);

  const limits = {
    max_duration: 3600,
    min_duration: 1,
    max_interval: 60000,
    min_interval: 10,
    max_payload_size: 1048576,
    max_concurrent_streams: 100,
    write_timeout_ms: 15000,
  };

  const limitsElement = document.getElementById('server-limits');

  function renderLimits(): void {
    if (!limitsElement) return;
    limitsElement.textContent = [
      `duration ≤ ${limits.max_duration} s`,
      `interval ${limits.min_interval}–${limits.max_interval} ms`,
      `payload ≤ ${limits.max_payload_size} B`,
      `streams ≤ ${limits.max_concurrent_streams}`,
      `write timeout ${limits.write_timeout_ms} ms`,
    ].join(' · ');
  }

  const suite = new Suite({
    grid: requireElement('protocol-grid'),
    intervalChart,
    throughputChart,
    peaksChart,
    log,
    onRunningChanged: (running) => {
      startButton.disabled = running;
      stopButton.disabled = !running;
      untilStoppedInput.disabled = running;
      autoReconnectInput.disabled = running;
      for (const input of protocolInputs) input.disabled = running;
      durationGroup.setDisabled(running);
      intervalGroup.setDisabled(running);
      payloadGroup.setDisabled(running);

      setText(runState, running ? t('run.running') : t('run.stopped'));
      cfgNotice.textContent = running ? t('cfg.locked') : t('cfg.ready');
      cfgNotice.className = running ? 'notice notice-locked' : 'notice';
    },
  });

  // --- server status ------------------------------------------------------
  let serverInfo = { version: '', uptime: 0, online: false };

  function refreshServerChip(): void {
    if (!serverInfo.online) {
      setText(serverChip, t('server.offline'));
      serverChip.className = 'server-chip offline';
      return;
    }
    const uptime = `${Math.floor(serverInfo.uptime / 60)}m ${serverInfo.uptime % 60}s`;
    setText(serverChip, t('server.online', { version: serverInfo.version, uptime }));
    serverChip.className = 'server-chip online';
  }

  async function pollHealth(): Promise<void> {
    try {
      const health = await fetchHealth();
      serverInfo = { version: health.version, uptime: health.uptime_seconds, online: true };
    } catch {
      serverInfo = { ...serverInfo, online: false };
    }
    refreshServerChip();
  }

  // --- language -----------------------------------------------------------
  const langButton = requireElement<HTMLButtonElement>('lang-toggle');
  langButton.addEventListener('click', () => toggleLang());
  onLangChange(() => {
    suite.renderLabels();
    refreshServerChip();
    setText(runState, suite.isRunning ? t('run.running') : t('run.stopped'));
    setText(runNote, t(suite.isRunning ? 'run.noteAuto' : 'run.noteStopped'));
    cfgNotice.textContent = suite.isRunning ? t('cfg.locked') : t('cfg.ready');
    renderLimits();
  });

  // --- start / stop -------------------------------------------------------
  function collectProtocols(): Protocol[] {
    const chosen: Protocol[] = [];
    for (const input of protocolInputs) {
      const value = input.value as Protocol;
      if (input.checked && PROTOCOLS.includes(value)) chosen.push(value);
    }
    return chosen;
  }

  startButton.addEventListener('click', () => {
    const protocols = collectProtocols();
    if (protocols.length === 0) return;
    const params: StreamParams = {
      duration: durationGroup.current,
      interval: intervalGroup.current,
      payload_size: payloadGroup.current,
    };
    const untilStopped = untilStoppedInput.checked;
    log.add(
      'info',
      t('log.started', {
        protocols: protocols.map((protocol) => t(`proto.${protocol}`)).join(', '),
        duration: untilStopped ? t('unit.untilStopped') : `${params.duration} s`,
        interval: `${params.interval} ms`,
        payload: `${params.payload_size} B`,
      }),
    );
    setText(runNote, untilStopped ? t('run.noteAuto') : t('run.noteManual'));
    suite.start(protocols, params, untilStopped, autoReconnectInput.checked);
  });

  stopButton.addEventListener('click', () => {
    if (!suite.isRunning) return;
    log.add('info', t('log.stopped'));
    suite.stop();
  });

  requireElement('log-clear').addEventListener('click', () => log.clear());

  // --- initial load -------------------------------------------------------
  setText(runState, t('run.stopped'));
  cfgNotice.textContent = t('cfg.ready');
  refreshServerChip();
  await pollHealth();
  window.setInterval(() => void pollHealth(), 10000);

  try {
    const config: ConfigResponse = await fetchConfig();

    limits.max_duration = config.limits.max_duration;
    limits.min_duration = config.limits.min_duration;
    limits.max_interval = config.limits.max_interval;
    limits.min_interval = config.limits.min_interval;
    limits.max_payload_size = config.limits.max_payload_size;
    limits.max_concurrent_streams = config.limits.max_concurrent_streams;
    limits.write_timeout_ms = config.limits.write_timeout_ms;

    durationGroup.setBounds(limits.min_duration, limits.max_duration);
    intervalGroup.setBounds(limits.min_interval, limits.max_interval);
    payloadGroup.setBounds(0, limits.max_payload_size);

    const clamp = (value: number, min: number, max: number): number =>
      Math.min(max, Math.max(min, value));

    durationGroup.set(clamp(60, limits.min_duration, limits.max_duration));
    intervalGroup.set(clamp(config.defaults.interval, limits.min_interval, limits.max_interval));
    payloadGroup.set(clamp(config.defaults.payload_size, 0, limits.max_payload_size));

    log.add(
      'success',
      t('log.connected', {
        name: config.name,
        version: config.version,
        protocols: config.protocols.join(', '),
      }),
    );
    log.add(
      'info',
      t('log.limits', {
        duration: `${limits.max_duration} s`,
        minInterval: limits.min_interval,
        maxInterval: limits.max_interval,
        payload: `${limits.max_payload_size} B`,
        streams: limits.max_concurrent_streams,
        writeTimeout: limits.write_timeout_ms,
      }),
    );
    renderLimits();

    // Open the console straight into a test: every protocol, server defaults,
    // running until the user stops it. The duration sent to the server is its
    // own maximum, so "until stopped" still respects the documented limits.
    const autoParams: StreamParams = {
      duration: limits.max_duration,
      interval: intervalGroup.current,
      payload_size: payloadGroup.current,
    };
    untilStoppedInput.checked = true;

    log.add(
      'info',
      t('log.autoStarted', {
        protocols: PROTOCOLS.map((protocol) => t(`proto.${protocol}`)).join(', '),
        interval: `${autoParams.interval} ms`,
        payload: `${autoParams.payload_size} B`,
      }),
    );
    setText(runNote, t('run.noteAuto'));
    suite.start(PROTOCOLS, autoParams, true, false);
  } catch (error) {
    setText(runNote, t('server.offline'));
    log.add('error', t('log.configError', { detail: describeError(error) }));
  }
}

void boot();
