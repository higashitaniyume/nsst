import uPlot from 'uplot';

export interface ChartSeriesSpec {
  label: string;
  stroke: string;
  /** Render the series as isolated points instead of a line. */
  asPoints?: boolean;
}

export interface MultiChartOptions {
  /** Series definitions; the index order must stay stable across runs. */
  series: ChartSeriesSpec[];
  /** Formats a y value for the axis. */
  format: (value: number) => string;
  /** Formats an x value (elapsed seconds) for the axis. */
  formatX?: (value: number) => string;
  /** Rendered height in CSS pixels. */
  height?: number;
  /** Draws a dashed guide line at this y value. */
  threshold?: number;
  /**
   * Seconds of history to keep. Older points are dropped as the run advances, so
   * a chart always shows the most recent slice rather than the whole run.
   */
  windowSeconds?: number;
}

/** Default trailing window: a chart shows roughly the last minute. */
const DEFAULT_WINDOW_SECONDS = 60;

const AXIS_FONT = '11px ui-monospace, SFMono-Regular, Menlo, monospace';
const GRID = '#1d2634';
const AXIS = '#7d8fa6';

/**
 * A live uPlot chart carrying one series per protocol.
 *
 * Points are plotted against elapsed seconds since the suite started. Only a
 * trailing window is retained: once a run passes {@link DEFAULT_WINDOW_SECONDS}
 * the oldest points are dropped, so a long test keeps showing the recent minute
 * at full resolution instead of squeezing hours into a few pixels.
 */
export class MultiChart {
  private readonly plot: uPlot;
  private readonly seriesCount: number;
  private readonly root: HTMLElement;
  private readonly observer: ResizeObserver;
  private readonly height: number;
  private readonly windowSeconds: number;
  private xs: number[] = [];
  private ys: (number | null)[][];

  constructor(root: HTMLElement, options: MultiChartOptions) {
    this.root = root;
    this.height = options.height ?? 200;
    this.windowSeconds = options.windowSeconds ?? DEFAULT_WINDOW_SECONDS;
    this.seriesCount = options.series.length;
    this.ys = options.series.map(() => []);

    root.setAttribute('role', 'img');

    this.plot = new uPlot(
      {
        width: Math.max(320, root.clientWidth || 640),
        height: this.height,
        padding: [12, 16, 0, 0],
        legend: { show: true, live: true },
        cursor: { show: true, y: false },
        scales: { x: { time: false } },
        axes: [
          {
            stroke: AXIS,
            grid: { stroke: GRID, width: 1 },
            ticks: { stroke: GRID, width: 1 },
            font: AXIS_FONT,
            values: (_u, splits) =>
              splits.map((v) => (options.formatX ? options.formatX(v) : `${Math.round(v)}s`)),
          },
          {
            stroke: AXIS,
            grid: { stroke: GRID, width: 1 },
            ticks: { stroke: GRID, width: 1 },
            font: AXIS_FONT,
            size: 62,
            values: (_u, splits) => splits.map((v) => options.format(v)),
          },
        ],
        series: [
          {},
          ...options.series.map((spec) => ({
            label: spec.label,
            stroke: spec.stroke,
            width: spec.asPoints ? 0 : 1.6,
            points: {
              show: spec.asPoints === true,
              size: 7,
              width: 0,
              fill: spec.stroke,
            },
            fill: spec.asPoints ? undefined : `${spec.stroke}22`,
            spanGaps: false,
          })),
        ],
        hooks: {
          draw: [
            (u) => {
              const threshold = options.threshold;
              if (threshold === undefined) return;
              const { ctx } = u;
              const y = u.valToPos(threshold, 'y', true);
              if (!Number.isFinite(y)) return;
              ctx.save();
              ctx.strokeStyle = '#e0603c88';
              ctx.setLineDash([4, 4]);
              ctx.lineWidth = 1;
              ctx.beginPath();
              ctx.moveTo(u.bbox.left, y);
              ctx.lineTo(u.bbox.left + u.bbox.width, y);
              ctx.stroke();
              ctx.restore();
            },
          ],
        },
      },
      [[], ...options.series.map(() => [])],
      root,
    );

    this.observer = new ResizeObserver(() => this.resize());
    this.observer.observe(root);
  }

  /** Updates the accessible label; call again whenever the language changes. */
  setLabel(text: string): void {
    this.root.setAttribute('aria-label', text);
  }

  /** Updates the legend labels in place, without rebuilding the chart. */
  setSeriesLabels(labels: string[]): void {
    for (let i = 0; i < this.seriesCount; i += 1) {
      const label = labels[i];
      if (label !== undefined) this.plot.series[i + 1].label = label;
    }
    this.plot.setData(this.data());
  }

  /**
   * Appends one row. `values` must have exactly one entry per series; use `null`
   * for a protocol that produced no sample this tick.
   */
  push(x: number, values: (number | null)[]): void {
    if (!Number.isFinite(x)) return;
    this.xs.push(x);
    for (let i = 0; i < this.seriesCount; i += 1) {
      const value = values[i];
      this.ys[i]!.push(value === null || value === undefined || !Number.isFinite(value) ? null : value);
    }
    this.trim(x);
    this.plot.setData(this.data());
  }

  /** Removes every point. */
  clear(): void {
    this.xs = [];
    this.ys = this.ys.map(() => []);
    this.plot.setData(this.data());
  }

  destroy(): void {
    this.observer.disconnect();
    this.plot.destroy();
  }

  resize(): void {
    const width = Math.max(320, this.root.clientWidth);
    this.plot.setSize({ width, height: this.height });
  }

  private data(): uPlot.AlignedData {
    if (this.xs.length === 0) {
      return [[], ...this.ys.map(() => [])] as unknown as uPlot.AlignedData;
    }
    return [this.xs, ...this.ys] as unknown as uPlot.AlignedData;
  }

  /**
   * Drops the points that have fallen out of the trailing window. Every series is
   * trimmed by the same count, so they all stay aligned with the x axis.
   */
  private trim(latest: number): void {
    const cutoff = latest - this.windowSeconds;
    let drop = 0;
    while (drop < this.xs.length && this.xs[drop]! < cutoff) drop += 1;
    if (drop === 0) return;

    this.xs.splice(0, drop);
    for (const series of this.ys) series.splice(0, drop);
  }
}
