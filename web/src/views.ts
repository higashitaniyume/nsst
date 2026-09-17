import { formatBytes, formatClock, formatMilliseconds, formatRate } from './format.js';
import { applyStatic, t } from './i18n.js';
import { setClass, setText } from './ui.js';
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

function buildTable(
  titleKey: string,
  rows: readonly RowKey[],
): { block: HTMLElement; cells: CellMap } {
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

/**
 * One protocol card: a heading, the endpoint it uses, and the two metric tables
 * (connection & traffic, timing & gaps) plus a one line verdict.
 */
export class ProtocolCard {
  readonly root: HTMLElement;

  private readonly cells: CellMap;
  private readonly statePill: HTMLElement;
  private readonly verdict: HTMLElement;

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

    const verdict = document.createElement('p');
    verdict.className = 'proto-verdict';

    root.append(head, endpoint, traffic.block, timing.block, verdict);

    this.root = root;
    this.statePill = pill;
    this.verdict = verdict;
    this.cells = new Map([...traffic.cells, ...timing.cells]);
  }

  /** Re-applies every translated label after a language change. */
  renderLabels(): void {
    applyStatic(this.root);
  }

  /** Returns the card to its pre-run state. */
  reset(): void {
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
