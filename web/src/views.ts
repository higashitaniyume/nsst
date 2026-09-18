import { formatBytes, formatClock, formatMilliseconds, formatRate } from './format.js';
import { applyStatic, t } from './i18n.js';
import { setClass, setText } from './ui.js';
import { PROTOCOLS } from './types.js';
import type { MetricsSnapshot, Protocol, StreamParams } from './types.js';

/** Line colour per protocol, shared by the cards and the charts. */
export const PROTOCOL_COLORS: Record<Protocol, string> = {
  'http-stream': '#4f9cf9',
  sse: '#39c07a',
  websocket: '#c792ea',
};

/** Stream endpoint each card talks to, shown under its title. */
export const PROTOCOL_ENDPOINTS: Record<Protocol, string> = {
  'http-stream': '/api/stream/http',
  sse: '/api/stream/sse',
  websocket: '/api/stream/ws',
};

// --------------------------------------------------------------------- tables

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
 * One protocol card, laid out as two columns:
 *
 *   .proto-data     the two metric tables, stacked
 *   .proto-charts   this protocol's own charts, stacked
 *
 * The verdict closes the card underneath both columns.
 */
export class ProtocolCard {
  readonly root: HTMLElement;
  /** Empty frames the suite fills with this protocol's own charts. */
  readonly chartSlots: ChartSlots;

  private readonly cells: CellMap;
  private readonly statePill: HTMLElement;
  private readonly verdict: HTMLElement;

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
    name.dataset.i18n = `proto.${protocol}`;
    name.textContent = t(`proto.${protocol}`);

    const pill = document.createElement('span');
    pill.className = 'pill state-idle';
    pill.textContent = t('state.idle');

    head.append(name, pill);

    const endpoint = document.createElement('code');
    endpoint.className = 'proto-endpoint';
    endpoint.textContent = PROTOCOL_ENDPOINTS[protocol];

    const traffic = buildTable('table.traffic', TRAFFIC_ROWS);
    const timing = buildTable('table.timing', TIMING_ROWS);

    // Every protocol carries its own charts, inside its own card.
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

    // Readings on the left, charts on the right. Each column stacks its own
    // contents, so the card uses its width instead of leaving a narrow table
    // adrift in a half-empty row.
    const data = document.createElement('div');
    data.className = 'proto-data';
    data.append(traffic.block, timing.block);

    const body = document.createElement('div');
    body.className = 'proto-body';
    body.append(data, charts);

    const verdict = document.createElement('p');
    verdict.className = 'proto-verdict';

    root.append(head, endpoint, body, verdict);

    this.root = root;
    this.chartSlots = { interval: interval.body, throughput: throughput.body, peaks: peaks.body };
    this.statePill = pill;
    this.verdict = verdict;
    this.cells = new Map([...traffic.cells, ...timing.cells]);
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
    setClass(this.statePill, 'pill', 'state-idle');

    for (const [, cell] of this.cells) {
      setText(cell, t('value.na'));
      setClass(cell, 'mono');
    }

    setText(this.verdict, '');
    setClass(this.verdict, 'proto-verdict');
  }

  update(
    snapshot: MetricsSnapshot,
    params: StreamParams,
    expectedFrames: number,
    untilStopped: boolean,
    running: boolean,
  ): void {
    this.last = { snapshot, params, expectedFrames, untilStopped, running };

    const state = snapshot.state;
    setText(this.statePill, t(`state.${state}`));
    setClass(this.statePill, 'pill', `state-${state}`);

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
    setClass(this.verdict, 'proto-verdict', `verdict-${verdict.level}`);
  }
}

// ------------------------------------------------------- cross-protocol table

/**
 * The frame-interval rows the overview compares.
 *
 * Deliberately narrow: the point of this table is to answer "did one protocol
 * behave differently" at a glance, not to repeat every reading the cards below
 * already carry. The connection-level counters (missing frames, reconnects) are
 * not interval data and stay on the cards.
 */
const OVERVIEW_ROWS = [
  'target',
  'intervalAvg',
  'intervalMin',
  'intervalMax',
  'longestGap',
  'interruptions',
] as const;

type OverviewRowKey = (typeof OVERVIEW_ROWS)[number];

/** One protocol's latest reading, as handed to the overview table. */
export interface OverviewEntry {
  protocol: Protocol;
  snapshot: MetricsSnapshot;
}

/**
 * The table at the top of the page: frame-interval readings for all three
 * protocols side by side, so a difference between them is visible without
 * scrolling back and forth between cards.
 *
 * The columns are fixed to the canonical protocol order whether or not a
 * protocol is part of the current run, so the table never reflows between runs;
 * a protocol that is not running simply reads "—".
 */
export class OverviewTable {
  private readonly cells = new Map<Protocol, Map<OverviewRowKey, HTMLTableCellElement>>();
  private last: { entries: readonly OverviewEntry[]; params: StreamParams } | null = null;

  constructor(private readonly root: HTMLElement) {
    root.append(this.build());
  }

  private build(): HTMLTableElement {
    const table = document.createElement('table');
    table.className = 'metrics-table overview-table';

    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');

    const corner = document.createElement('th');
    corner.scope = 'col';
    corner.dataset.i18n = 'overview.metric';
    corner.textContent = t('overview.metric');
    headRow.append(corner);

    for (const protocol of PROTOCOLS) {
      const th = document.createElement('th');
      th.scope = 'col';
      th.dataset.proto = protocol;
      th.dataset.i18n = `proto.${protocol}`;
      th.textContent = t(`proto.${protocol}`);
      headRow.append(th);
      this.cells.set(protocol, new Map());
    }
    thead.append(headRow);

    const tbody = document.createElement('tbody');
    for (const key of OVERVIEW_ROWS) {
      const tr = document.createElement('tr');

      const th = document.createElement('th');
      th.scope = 'row';
      th.dataset.i18n = `row.${key}`;
      th.textContent = t(`row.${key}`);
      tr.append(th);

      for (const protocol of PROTOCOLS) {
        const td = document.createElement('td');
        td.className = 'mono';
        td.textContent = t('value.na');
        tr.append(td);
        this.cells.get(protocol)?.set(key, td);
      }
      tbody.append(tr);
    }

    table.append(thead, tbody);
    return table;
  }

  private cell(protocol: Protocol, key: OverviewRowKey): HTMLTableCellElement | null {
    return this.cells.get(protocol)?.get(key) ?? null;
  }

  /** Clears every column back to "—". */
  reset(): void {
    this.last = null;
    for (const protocol of PROTOCOLS) {
      for (const key of OVERVIEW_ROWS) {
        const td = this.cell(protocol, key);
        setText(td, t('value.na'));
        setClass(td, 'mono');
      }
    }
  }

  /** Re-applies translated headers, then redraws values built in code. */
  renderLabels(): void {
    applyStatic(this.root);
    if (this.last) this.update(this.last.entries, this.last.params);
  }

  update(entries: readonly OverviewEntry[], params: StreamParams): void {
    this.last = { entries, params };

    const byProtocol = new Map(entries.map((entry) => [entry.protocol, entry.snapshot]));

    for (const protocol of PROTOCOLS) {
      const snapshot = byProtocol.get(protocol);

      if (!snapshot) {
        for (const key of OVERVIEW_ROWS) {
          const td = this.cell(protocol, key);
          setText(td, t('value.na'));
          setClass(td, 'mono');
        }
        continue;
      }

      setText(this.cell(protocol, 'target'), formatMilliseconds(params.interval));
      setText(this.cell(protocol, 'intervalAvg'), formatMilliseconds(snapshot.avgIntervalMs));
      setText(this.cell(protocol, 'intervalMin'), formatMilliseconds(snapshot.minIntervalMs));

      const maxInterval = this.cell(protocol, 'intervalMax');
      setText(maxInterval, formatMilliseconds(snapshot.maxIntervalMs));
      setClass(
        maxInterval,
        'mono',
        snapshot.maxIntervalMs !== null && snapshot.maxIntervalMs > snapshot.thresholdMs
          ? 'bad'
          : '',
      );

      const longestGap = this.cell(protocol, 'longestGap');
      setText(
        longestGap,
        snapshot.longestGapMs > 0 ? formatMilliseconds(snapshot.longestGapMs) : t('value.none'),
      );
      setClass(longestGap, 'mono', snapshot.longestGapMs > 0 ? 'bad' : 'ok');

      const interruptions = this.cell(protocol, 'interruptions');
      setText(interruptions, String(snapshot.interruptions));
      setClass(interruptions, 'mono', snapshot.interruptions > 0 ? 'bad' : 'ok');
    }
  }
}
