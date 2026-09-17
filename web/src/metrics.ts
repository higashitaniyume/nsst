import type { ConnectionState, MetricsSnapshot, StreamFrame } from './types.js';

/**
 * Result of feeding one frame into the collector, so that the caller can drive
 * the charts without recomputing anything.
 */
export interface FrameObservation {
  /** Inter-arrival time since the previous frame, null for the first frame. */
  intervalMs: number | null;
  /** True when this interval qualifies as a stream interruption. */
  stall: boolean;
  /** Milliseconds since the run started at which the frame arrived. */
  arrivalMs: number;
}

/** One point produced by {@link MetricsCollector.tick}. */
export interface TickResult {
  t: number;
  intervalMs: number;
  throughputBps: number;
  framesDelta: number;
}

/**
 * A stall threshold derived from the requested interval.
 *
 * A gap is only interesting when it is clearly larger than the pacing the server
 * promised, so the threshold scales with the interval and always leaves room for
 * a single late frame caused by scheduler jitter.
 */
export function stallThresholdMs(intervalMs: number): number {
  return Math.max(3 * intervalMs, intervalMs + 250);
}

/**
 * Accumulates client side measurements for one test run.
 *
 * Everything reported here is derived from frames that actually arrived. The
 * collector never predicts, extrapolates or simulates a measurement: if no frame
 * arrives, the only thing that changes is the length of the gap.
 *
 * Terminology: an "interruption" is an application level gap between two frames.
 * It is *not* TCP packet loss; the browser cannot observe retransmissions, and a
 * gap means the server did not deliver a frame on schedule (or the frames were
 * delayed in the network), not that a packet was lost on the wire.
 */
export class MetricsCollector {
  private readonly gapThresholdMs: number;
  private readonly windowMs = 2000;

  private state: ConnectionState = 'idle';
  private detail = '';

  private startedAt = 0;
  private lastTick: number;
  private lastArrival: number | null = null;
  private firstFrameAt: number | null = null;
  private lastSequence: number | null = null;
  private lastInterval: number | null = null;

  private frames = 0;
  private bytes = 0;
  private payloadBytes = 0;

  private intervalSum = 0;
  private intervalCount = 0;
  private minInterval: number | null = null;
  private maxInterval: number | null = null;

  private maxGapMs = 0;
  private longestGapMs = 0;
  private totalGapMs = 0;
  private interruptions = 0;

  private reconnects = 0;
  private sequenceGaps = 0;
  private missingFrames = 0;
  private outOfOrderFrames = 0;
  private integrityErrors = 0;

  private window: Array<{ t: number; bytes: number }> = [];
  private tickFrames = 0;
  private tickIntervalSum = 0;
  private tickIntervalCount = 0;
  private tickBytes = 0;

  constructor(intervalMs: number) {
    this.gapThresholdMs = stallThresholdMs(intervalMs);
    this.lastTick = performance.now();
  }

  /** The inter-arrival gap that counts as a stream interruption. */
  get thresholdMs(): number {
    return this.gapThresholdMs;
  }

  /** Resets every counter and marks the beginning of the run. */
  start(now: number = performance.now()): void {
    this.startedAt = now;
    this.lastTick = now;
    this.lastArrival = null;
    this.firstFrameAt = null;
    this.lastSequence = null;
    this.lastInterval = null;

    this.frames = 0;
    this.bytes = 0;
    this.payloadBytes = 0;

    this.intervalSum = 0;
    this.intervalCount = 0;
    this.minInterval = null;
    this.maxInterval = null;

    this.maxGapMs = 0;
    this.longestGapMs = 0;
    this.totalGapMs = 0;
    this.interruptions = 0;

    this.reconnects = 0;
    this.sequenceGaps = 0;
    this.missingFrames = 0;
    this.outOfOrderFrames = 0;
    this.integrityErrors = 0;

    this.window = [];
    this.tickFrames = 0;
    this.tickIntervalSum = 0;
    this.tickIntervalCount = 0;
    this.tickBytes = 0;

    this.state = 'connecting';
    this.detail = '';
  }

  setState(state: ConnectionState, detail = ''): void {
    this.state = state;
    this.detail = detail;
  }

  getState(): ConnectionState {
    return this.state;
  }

  /** Milliseconds since the run started. */
  elapsedMs(now: number = performance.now()): number {
    return Math.max(0, now - this.startedAt);
  }

  /**
   * Records one decoded frame.
   *
   * @param frame    the decoded wire frame
   * @param wireBytes application level bytes received for this frame
   */
  onFrame(
    frame: StreamFrame,
    wireBytes: number,
    now: number = performance.now(),
  ): FrameObservation {
    const arrival = Math.max(0, now - this.startedAt);
    if (this.firstFrameAt === null) {
      this.firstFrameAt = arrival;
    }

    let intervalMs: number | null = null;
    let stall = false;

    if (this.lastArrival !== null) {
      const delta = arrival - this.lastArrival;
      // A negative delta can only come from a clock that moved backwards
      // (performance.now() is monotonic, but a reconnect may reset the baseline).
      if (delta >= 0) {
        intervalMs = delta;
        this.intervalSum += delta;
        this.intervalCount += 1;
        this.tickIntervalSum += delta;
        this.tickIntervalCount += 1;

        if (this.minInterval === null || delta < this.minInterval) this.minInterval = delta;
        if (this.maxInterval === null || delta > this.maxInterval) this.maxInterval = delta;
        if (delta > this.maxGapMs) this.maxGapMs = delta;
        if (delta > this.gapThresholdMs) {
          stall = true;
          this.interruptions += 1;
          this.totalGapMs += delta;
          if (delta > this.longestGapMs) this.longestGapMs = delta;
        }
      }
    }

    this.lastArrival = arrival;
    this.lastInterval = intervalMs;

    // Frame integrity: the server states how many payload bytes it wrote, so the
    // client can verify that nothing was truncated or re-encoded in transit.
    if (frame.payload_size > 0) {
      if ((frame.payload?.length ?? 0) !== frame.payload_size) {
        this.integrityErrors += 1;
      }
    } else if (frame.payload !== undefined && frame.payload.length !== 0) {
      this.integrityErrors += 1;
    }

    // Sequence integrity, tracked within a single connection only.
    if (this.lastSequence !== null) {
      if (frame.sequence > this.lastSequence + 1) {
        this.sequenceGaps += 1;
        this.missingFrames += frame.sequence - this.lastSequence - 1;
      } else if (frame.sequence <= this.lastSequence) {
        this.outOfOrderFrames += 1;
      }
    }
    this.lastSequence = frame.sequence;

    this.frames += 1;
    this.bytes += wireBytes;
    this.payloadBytes += frame.payload_size;
    this.tickFrames += 1;
    this.tickBytes += wireBytes;

    this.window.push({ t: arrival, bytes: wireBytes });
    this.pruneWindow(arrival);

    return { intervalMs, stall, arrivalMs: arrival };
  }

  /**
   * Notifies the collector that a new connection was established for the same
   * test run. The sequence and gap baselines restart: numbering restarts at one
   * on the server, and the downtime of a reconnect is reported as a reconnect
   * rather than as a stream gap.
   */
  noteReconnect(): void {
    this.reconnects += 1;
    this.lastSequence = null;
    this.lastArrival = null;
    this.lastInterval = null;
  }

  /** Produces one chart point and resets the per tick accumulators. */
  tick(now: number = performance.now()): TickResult {
    const t = this.elapsedMs(now) / 1000;
    const dtSeconds = Math.max(0, now - this.lastTick) / 1000;

    let intervalMs: number;
    if (this.tickIntervalCount > 0) {
      intervalMs = this.tickIntervalSum / this.tickIntervalCount;
    } else if (this.lastArrival !== null) {
      // No frame arrived during this tick: report the gap as it grows so that a
      // stall is visible on the chart while it is still happening.
      intervalMs = Math.max(0, this.elapsedMs(now) - this.lastArrival);
    } else {
      intervalMs = 0;
    }

    const result: TickResult = {
      t,
      intervalMs,
      throughputBps: dtSeconds > 0 ? this.tickBytes / dtSeconds : 0,
      framesDelta: this.tickFrames,
    };

    this.lastTick = now;
    this.tickFrames = 0;
    this.tickIntervalSum = 0;
    this.tickIntervalCount = 0;
    this.tickBytes = 0;

    return result;
  }

  /**
   * Accounts for a gap that is still open when the run ends. Without this, a
   * connection that dies mid-stall would report the stall as if it never
   * happened.
   */
  closeOpenGap(now: number = performance.now()): void {
    if (this.lastArrival === null) return;
    const ongoing = Math.max(0, this.elapsedMs(now) - this.lastArrival);
    if (ongoing <= 0) return;

    if (ongoing > this.maxGapMs) this.maxGapMs = ongoing;
    if (ongoing > this.gapThresholdMs) {
      this.interruptions += 1;
      this.totalGapMs += ongoing;
      if (ongoing > this.longestGapMs) this.longestGapMs = ongoing;
    }
    this.lastArrival = null;
  }

  /** The inter-arrival time of the most recent frame, if there was one. */
  get lastIntervalMs(): number | null {
    return this.lastInterval;
  }

  private pruneWindow(arrival: number): void {
    const cutoff = arrival - this.windowMs;
    let drop = 0;
    while (drop < this.window.length && this.window[drop]!.t < cutoff) drop += 1;
    if (drop > 0) this.window.splice(0, drop);
  }

  snapshot(now: number = performance.now()): MetricsSnapshot {
    const elapsed = this.elapsedMs(now);
    const seconds = elapsed / 1000;

    let currentThroughputBps = 0;
    if (this.window.length > 0) {
      const spanMs = Math.max(200, this.elapsedMs(now) - this.window[0]!.t);
      let total = 0;
      for (const entry of this.window) total += entry.bytes;
      currentThroughputBps = total / (spanMs / 1000);
    }

    return {
      state: this.state,
      detail: this.detail,
      elapsedMs: elapsed,
      frames: this.frames,
      bytes: this.bytes,
      payloadBytes: this.payloadBytes,
      avgThroughputBps: seconds > 0 ? this.bytes / seconds : 0,
      currentThroughputBps,
      minIntervalMs: this.minInterval,
      avgIntervalMs: this.intervalCount > 0 ? this.intervalSum / this.intervalCount : null,
      maxIntervalMs: this.maxInterval,
      interruptions: this.interruptions,
      thresholdMs: this.gapThresholdMs,
      longestGapMs: this.longestGapMs,
      totalGapMs: this.totalGapMs,
      maxGapMs: this.maxGapMs,
      firstFrameLatencyMs: this.firstFrameAt,
      reconnects: this.reconnects,
      sequenceGaps: this.sequenceGaps,
      missingFrames: this.missingFrames,
      outOfOrderFrames: this.outOfOrderFrames,
      integrityErrors: this.integrityErrors,
    };
  }
}
