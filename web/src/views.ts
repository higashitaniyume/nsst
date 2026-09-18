import { formatBytes, formatClock, formatMilliseconds, formatRate } from './format.js';
import { applyStatic, t } from './i18n.js';
import { setClass, setText } from './ui.js';
import type { MetricsSnapshot, Protocol, StreamParams } from './types.js';

/** Which of the two presentations is on screen. */
export type ViewMode = 'simple' | 'pro';

/** Line colour per protocol, shared by the cards and the charts. */
export const PROTOCOL_COLORS: Record<Protocol, string> = {
  'http-stream': '#4f9cf9',
  sse: '#39c07a',
  websocket: '#c792ea',
};

/** Stream endpoint each card talks to, shown under its title in the pro view. */
export const PROTOCOL_ENDPOINTS: Record<Protocol, string> = {
  'http-stream': '/api/stream/http',
  sse: '/api/stream/sse',
  websocket: '/api/stream/ws',
};

// ---------------------------------------------------------------- pro tables

/** Table 1: connection and traffic. */
const TRAFFIC_ROWS = [
  'status',
  'frames',
  'bytes',
  'tpAvg',
  'tpNow',
  'firstFrame',
  'integrity',
] as const;

/** Table 2: timing and gaps. */
const TIMING_ROWS = [
  'target',
  'intervalAvg',
  'intervalMin',
  'intervalMax',
  'longestGap',
  'interruptions',
  'missing',
  'reconnects',
] as const;

type RowKey = (typeof TRAFFIC_ROWS)[number] | (typeof TIMING_ROWS)[number];

type CellMap = Map<RowKey, HTMLTableCellElement>;

// ------------------------------------------------------------- simple tables

/**
 * The plain-language rows. They say the same things as the pro tables but in
 * words someone who has never read a protocol spec would use.
 */
const SIMPLE_ROWS = ['frames', 'size', 'pace', 'stalls', 'worst', 'drops'] as const;

type SimpleRowKey = (typeof SIMPLE_ROWS)[number];

type SimpleCellMap = Map<SimpleRowKey, HTMLTableCellElement>;

// ---------------------------------------------------------------- formatting

/** Counts read better with a thousands separator: 1,204 rather than 1204. */
function formatCount(value: number): string {
  return Math.max(0, Math.round(value)).toLocaleString();
}

/**
 * A duration a layperson can picture: "120 毫秒", "1.4 秒", "2 分钟". The pro
 * view keeps the raw millisecond figure instead.
 */
function friendlyDuration(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms)) return t('value.na');
  if (ms < 1000) return `${Math.round(ms)} ${t('unit.ms')}`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)} ${t('unit.seconds')}`;
  return `${Math.round(ms / 60000)} ${t('unit.minutes')}`;
}

/** Decimal units, which is what people expect from a file size. */
function friendlyBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return t('value.na');
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

// ------------------------------------------------------------------ verdicts

export interface Verdict {
  level: 'good' | 'warn' | 'bad';
  text: string;
}

/**
 * A short, honest summary for one protocol. It never claims packet loss: what
 * the client can observe is a gap between frame arrivals, and that is all it
 * reports.
 */
export function verdictFor(
  snapshot: MetricsSnapshot,
  params: StreamParams,
  expectedFrames: number,
  untilStopped: boolean,
  running: boolean,
): Verdict {
  const threshold = formatMilliseconds(snapshot.thresholdMs);

  if (snapshot.frames === 0) {
    return {
      level: snapshot.state === 'error' ? 'bad' : 'warn',
      text: t('verdict.noFrames'),
    };
  }

  if (snapshot.interruptions > 0) {
    return {
      level: 'bad',
      text: t('verdict.interrupted', {
        count: snapshot.interruptions,
        gap: formatMilliseconds(snapshot.longestGapMs),
        interval: formatMilliseconds(params.interval),
      }),
    };
  }

  if (untilStopped) {
    return {
      level: 'good',
      text: t('verdict.untilStopped', {
        frames: snapshot.frames,
        elapsed: formatClock(snapshot.elapsedMs),
        threshold,
      }),
    };
  }

  if (running) {
    return {
      level: 'good',
      text: t('verdict.noGapYet', { frames: snapshot.frames, threshold }),
    };
  }

  if (snapshot.state === 'completed' && snapshot.frames >= expectedFrames) {
    return {
      level: 'good',
      text: t('verdict.stable', { frames: snapshot.frames, threshold }),
    };
  }

  return {
    level: 'warn',
    text: t('verdict.endedEarly', { frames: snapshot.frames, expected: expectedFrames }),
  };
}

export interface SimpleVerdict {
  level: 'good' | 'warn' | 'bad' | 'idle';
  /** Short badge next to the card title. */
  status: string;
  /** One sentence explaining what that means. */
  text: string;
}

/**
 * The same judgement as {@link verdictFor}, phrased without a single technical
 * term. Thresholds are deliberately hidden: "it stalled twice for up to 1.4
 * seconds" is actionable, "the gap exceeded 3x the target interval" is not.
 */
export function simpleVerdictFor(snapshot: MetricsSnapshot): SimpleVerdict {
  if (snapshot.state === 'idle') {
    return { level: 'idle', status: t('simple.status.idle'), text: t('simple.verdict.noData') };
  }

  if (snapshot.frames === 0) {
    if (snapshot.state === 'error') {
      return { level: 'bad', status: t('simple.status.dead'), text: t('simple.verdict.error') };
    }
    return {
      level: 'idle',
      status: t('simple.status.connecting'),
      text: t('simple.verdict.noData'),
    };
  }

  // Data arrived, then the connection died: that is worth flagging even when no
  // single gap crossed the stall threshold.
  if (snapshot.state === 'error' || snapshot.state === 'disconnected') {
    return { level: 'bad', status: t('simple.status.broke'), text: t('simple.verdict.broke') };
  }

  const gap = friendlyDuration(snapshot.longestGapMs);

  if (snapshot.interruptions === 0) {
    return { level: 'good', status: t('simple.status.flowing'), text: t('simple.verdict.flowing') };
  }
  if (snapshot.interruptions <= 2) {
    return {
      level: 'warn',
      status: t('simple.status.minor'),
      text: t('simple.verdict.minor', { gap }),
    };
  }
  return {
    level: 'bad',
    status: t('simple.status.laggy'),
    text: t('simple.verdict.laggy', { count: snapshot.interruptions, gap }),
  };
}

export interface SimpleSummary {
  level: 'good' | 'warn' | 'idle';
  text: string;
}

/** One sentence covering all three protocols, for the panel above the cards. */
export function simpleSummaryFor(
  entries: readonly { protocol: Protocol; snapshot: MetricsSnapshot }[],
): SimpleSummary {
  const measured = entries.filter((entry) => entry.snapshot.frames > 0);
  if (measured.length === 0) {
    return { level: 'idle', text: t('simple.summaryWaiting') };
  }

  const troubled = measured.filter(
    (entry) =>
      entry.snapshot.interruptions > 0 ||
      entry.snapshot.state === 'error' ||
      entry.snapshot.state === 'disconnected',
  );
  if (troubled.length === 0) {
    return { level: 'good', text: t('simple.summaryAll') };
  }

  return {
    level: 'warn',
    text: t('simple.summarySome', {
      protocols: troubled.map((entry) => t(`simple.name.${entry.protocol}`)).join(t('list.sep')),
    }),
  };
}

// -------------------------------------------------------------- card builders

function buildTable(titleKey: string, rows: readonly RowKey[]): { block: HTMLElement; cells: CellMap } {
  const block = document.createElement('div');
  block.className = 'metrics-block';

  const title = document.createElement('h3');
  title.className = 'table-title';
  title.dataset.i18n = titleKey;
  title.textContent = t(titleKey);

  const table = document.createElement('table');
  table.className = 'metrics-table';

  const tbody = document.createElement('tbody');
  const cells: CellMap = new Map();

  for (const key of rows) {
    const tr = document.createElement('tr');

    const th = document.createElement('th');
    th.scope = 'row';
    th.dataset.i18n = `row.${key}`;
    th.textContent = t(`row.${key}`);

    const td = document.createElement('td');
    td.className = 'mono';
    td.textContent = t('value.na');

    tr.append(th, td);
    tbody.append(tr);
    cells.set(key, td);
  }

  table.append(tbody);
  block.append(title, table);
  return { block, cells };
}

function buildSimpleBlock(): {
  block: HTMLElement;
  cells: SimpleCellMap;
  verdict: HTMLElement;
} {
  const block = document.createElement('div');
  block.className = 'proto-simple';

  const table = document.createElement('table');
  table.className = 'simple-table';

  const tbody = document.createElement('tbody');
  const cells: SimpleCellMap = new Map();

  for (const key of SIMPLE_ROWS) {
    const tr = document.createElement('tr');

    const th = document.createElement('th');
    th.scope = 'row';
    th.dataset.i18n = `simple.row.${key}`;
    th.textContent = t(`simple.row.${key}`);

    const td = document.createElement('td');
    td.className = 'simple-value';
    td.textContent = t('value.na');

    tr.append(th, td);
    tbody.append(tr);
    cells.set(key, td);
  }

  table.append(tbody);

  const verdict = document.createElement('p');
  verdict.className = 'simple-verdict';

  block.append(table);
  return { block, cells, verdict };
}

/**
 * The frame for one chart inside a protocol card. The card owns the markup, the
 * suite owns the uPlot instance that goes into `body` — that keeps chart
 * construction out of the view layer, which has no data to plot yet.
 */
function buildChartBlock(
  id: string,
  titleKey: string,
  hintKey: string,
  collapsible = false,
): { root: HTMLElement; body: HTMLElement } {
  const root = document.createElement(collapsible ? 'details' : 'div');
  root.className = collapsible ? 'mini-chart mini-chart-collapsible' : 'mini-chart';

  const title = document.createElement(collapsible ? 'summary' : 'h3');
  title.className = 'table-title';
  title.dataset.i18n = titleKey;
  title.textContent = t(titleKey);

  const hint = document.createElement('p');
  hint.className = 'hint';
  hint.dataset.i18n = hintKey;
  hint.textContent = t(hintKey);

  const body = document.createElement('div');
  body.className = 'chart';
  body.id = id;

  root.append(title, hint, body);
  return { root, body };
}

/** Where the suite mounts each protocol's own charts. */
export interface ChartSlots {
  interval: HTMLElement;
  throughput: HTMLElement;
  peaks: HTMLElement;
}

// ------------------------------------------------------------------ the card

/**
 * One protocol card. It carries both presentations at once and CSS decides which
 * one is visible, so flipping modes is instant and never loses a reading.
 *
 *   .proto-simple   plain language, no jargon
 *   .proto-pro      the two full metric tables
 */
export class ProtocolCard {
  readonly root: HTMLElement;
  /** Empty frames the suite fills with this protocol's own charts. */
  readonly chartSlots: ChartSlots;

  private readonly cells: CellMap;
  private readonly simpleCells: SimpleCellMap;
  private readonly statePill: HTMLElement;
  private readonly simplePill: HTMLElement;
  private readonly verdict: HTMLElement;
  private readonly simpleVerdict: HTMLElement;

  /** Kept so a language switch can redraw text that is not in the markup. */
  private last: {
    snapshot: MetricsSnapshot;
    params: StreamParams;
    expectedFrames: number;
    untilStopped: boolean;
    running: boolean;
  } | null = null;

  constructor(readonly protocol: Protocol) {
    const root = document.createElement('article');
    root.className = 'panel proto-card';
    root.dataset.proto = protocol;

    const head = document.createElement('header');
    head.className = 'proto-head';

    const name = document.createElement('h2');
    name.className = 'proto-name';

    const simpleName = document.createElement('span');
    simpleName.className = 'only-simple';
    simpleName.dataset.i18n = `simple.name.${protocol}`;
    simpleName.textContent = t(`simple.name.${protocol}`);

    const proName = document.createElement('span');
    proName.className = 'only-pro';
    proName.dataset.i18n = `proto.${protocol}`;
    proName.textContent = t(`proto.${protocol}`);

    name.append(simpleName, proName);

    const simplePill = document.createElement('span');
    simplePill.className = 'pill only-simple simple-pill';

    const pill = document.createElement('span');
    pill.className = 'pill only-pro state-idle';
    pill.textContent = t('state.idle');

    head.append(name, simplePill, pill);

    const endpoint = document.createElement('code');
    endpoint.className = 'proto-endpoint only-pro';
    endpoint.textContent = PROTOCOL_ENDPOINTS[protocol];

    const about = document.createElement('p');
    about.className = 'simple-about only-simple';
    about.dataset.i18n = `simple.about.${protocol}`;
    about.textContent = t(`simple.about.${protocol}`);

    const traffic = buildTable('table.traffic', TRAFFIC_ROWS);
    const timing = buildTable('table.timing', TIMING_ROWS);

    const verdict = document.createElement('p');
    verdict.className = 'proto-verdict only-pro';

    const pro = document.createElement('div');
    pro.className = 'proto-pro only-pro';
    pro.append(traffic.block, timing.block);

    const simple = buildSimpleBlock();
    simple.block.classList.add('only-simple');
    simple.verdict.classList.add('only-simple');

    // Every protocol carries its own charts, and they sit inside that protocol's
    // block rather than in one shared panel at the bottom of the page. They are
    // not part of either view, so they show in both.
    const interval = buildChartBlock(
      `chart-interval-${protocol}`,
      'chart.interval',
      'chart.intervalHint',
    );
    const throughput = buildChartBlock(
      `chart-throughput-${protocol}`,
      'chart.throughput',
      'chart.throughputHint',
    );
    const peaks = buildChartBlock(`chart-peaks-${protocol}`, 'chart.peaks', 'chart.peaksHint', true);

    const charts = document.createElement('div');
    charts.className = 'proto-charts';
    charts.append(interval.root, throughput.root, peaks.root);

    // Numbers first, then the charts, then the conclusion — in both views.
    root.append(head, endpoint, about, simple.block, pro, charts, simple.verdict, verdict);

    this.root = root;
    this.chartSlots = { interval: interval.body, throughput: throughput.body, peaks: peaks.body };
    this.statePill = pill;
    this.simplePill = simplePill;
    this.verdict = verdict;
    this.simpleVerdict = simple.verdict;
    this.cells = new Map([...traffic.cells, ...timing.cells]);
    this.simpleCells = simple.cells;
  }

  /**
   * Re-applies every translated label, then redraws the values whose text is
   * built in code rather than declared in the markup.
   */
  renderLabels(): void {
    applyStatic(this.root);
    if (this.last) {
      this.update(
        this.last.snapshot,
        this.last.params,
        this.last.expectedFrames,
        this.last.untilStopped,
        this.last.running,
      );
    }
  }

  /** Returns the card to its pre-run state. */
  reset(): void {
    this.last = null;

    setText(this.statePill, t('state.idle'));
    setClass(this.statePill, 'pill', 'only-pro', 'state-idle');

    setText(this.simplePill, t('simple.status.idle'));
    setClass(this.simplePill, 'pill', 'only-simple', 'simple-pill');

    for (const [, cell] of this.cells) {
      setText(cell, t('value.na'));
      setClass(cell, 'mono');
    }
    for (const [, cell] of this.simpleCells) {
      setText(cell, t('value.na'));
    }

    setText(this.verdict, '');
    setClass(this.verdict, 'proto-verdict', 'only-pro');

    setText(this.simpleVerdict, '');
    setClass(this.simpleVerdict, 'simple-verdict', 'only-simple');
  }

  update(
    snapshot: MetricsSnapshot,
    params: StreamParams,
    expectedFrames: number,
    untilStopped: boolean,
    running: boolean,
  ): void {
    this.last = { snapshot, params, expectedFrames, untilStopped, running };

    this.updatePro(snapshot, params, expectedFrames, untilStopped, running);
    this.updateSimple(snapshot);
  }

  // --- professional presentation ------------------------------------------

  private updatePro(
    snapshot: MetricsSnapshot,
    params: StreamParams,
    expectedFrames: number,
    untilStopped: boolean,
    running: boolean,
  ): void {
    const state = snapshot.state;
    setText(this.statePill, t(`state.${state}`));
    setClass(this.statePill, 'pill', 'only-pro', `state-${state}`);

    const cell = (key: RowKey): HTMLTableCellElement | null => this.cells.get(key) ?? null;

    const status = cell('status');
    setText(status, t(`state.${state}`));
    setClass(status, 'mono', `state-${state}`);

    setText(
      cell('frames'),
      expectedFrames > 0
        ? t('value.bounded', { actual: snapshot.frames, expected: expectedFrames })
        : String(snapshot.frames),
    );

    setText(cell('bytes'), formatBytes(snapshot.bytes));
    setText(cell('tpAvg'), formatRate(snapshot.avgThroughputBps));
    setText(cell('tpNow'), formatRate(snapshot.currentThroughputBps));
    setText(cell('firstFrame'), formatMilliseconds(snapshot.firstFrameLatencyMs));

    const integrity = cell('integrity');
    if (snapshot.integrityErrors === 0) {
      setText(integrity, t('value.allVerified'));
      setClass(integrity, 'mono', 'ok');
    } else {
      setText(integrity, t('value.failedVerification', { count: snapshot.integrityErrors }));
      setClass(integrity, 'mono', 'bad');
    }

    setText(cell('target'), formatMilliseconds(params.interval));
    setText(cell('intervalAvg'), formatMilliseconds(snapshot.avgIntervalMs));
    setText(cell('intervalMin'), formatMilliseconds(snapshot.minIntervalMs));

    const maxInterval = cell('intervalMax');
    setText(maxInterval, formatMilliseconds(snapshot.maxIntervalMs));
    setClass(
      maxInterval,
      'mono',
      snapshot.maxIntervalMs !== null && snapshot.maxIntervalMs > snapshot.thresholdMs ? 'bad' : '',
    );

    const longestGap = cell('longestGap');
    setText(
      longestGap,
      snapshot.longestGapMs > 0 ? formatMilliseconds(snapshot.longestGapMs) : t('value.none'),
    );
    setClass(longestGap, 'mono', snapshot.longestGapMs > 0 ? 'bad' : 'ok');

    const interruptions = cell('interruptions');
    setText(interruptions, String(snapshot.interruptions));
    setClass(interruptions, 'mono', snapshot.interruptions > 0 ? 'bad' : 'ok');

    const missing = cell('missing');
    setText(missing, String(snapshot.missingFrames));
    setClass(missing, 'mono', snapshot.missingFrames > 0 ? 'warn' : '');

    setText(cell('reconnects'), String(snapshot.reconnects));

    const verdict = verdictFor(snapshot, params, expectedFrames, untilStopped, running);
    setText(this.verdict, verdict.text);
    setClass(this.verdict, 'proto-verdict', 'only-pro', `verdict-${verdict.level}`);
  }

  // --- plain-language presentation ----------------------------------------

  private updateSimple(snapshot: MetricsSnapshot): void {
    const verdict = simpleVerdictFor(snapshot);

    setText(this.simplePill, verdict.status);
    setClass(this.simplePill, 'pill', 'only-simple', 'simple-pill', `simple-${verdict.level}`);

    setText(this.simpleVerdict, verdict.text);
    setClass(this.simpleVerdict, 'simple-verdict', 'only-simple', `verdict-${verdict.level}`);

    const cell = (key: SimpleRowKey): HTMLTableCellElement | null =>
      this.simpleCells.get(key) ?? null;

    setText(cell('frames'), t('simple.value.frames', { count: formatCount(snapshot.frames) }));
    setText(cell('size'), friendlyBytes(snapshot.bytes));

    setText(
      cell('pace'),
      snapshot.avgIntervalMs === null
        ? t('value.na')
        : t('simple.value.pace', { interval: friendlyDuration(snapshot.avgIntervalMs) }),
    );

    const stalls = cell('stalls');
    if (snapshot.interruptions === 0) {
      setText(stalls, t('simple.value.noStalls'));
      setClass(stalls, 'simple-value', 'ok');
    } else {
      setText(stalls, t('simple.value.stalls', { count: snapshot.interruptions }));
      setClass(stalls, 'simple-value', 'bad');
    }

    const worst = cell('worst');
    if (snapshot.longestGapMs <= 0) {
      setText(worst, t('simple.value.noWorst'));
      setClass(worst, 'simple-value', 'ok');
    } else {
      setText(worst, friendlyDuration(snapshot.longestGapMs));
      setClass(worst, 'simple-value', 'bad');
    }

    const drops = cell('drops');
    if (snapshot.reconnects === 0) {
      setText(drops, t('simple.value.noDrops'));
      setClass(drops, 'simple-value', 'ok');
    } else {
      setText(drops, t('simple.value.drops', { count: snapshot.reconnects }));
      setClass(drops, 'simple-value', 'warn');
    }
  }
}
