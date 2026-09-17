/**
 * Wire types shared with the Go backend.
 *
 * The frame model is deliberately identical across all three protocols, so a
 * measurement taken over HTTP streaming is directly comparable with the same
 * measurement taken over SSE or WebSocket.
 */

/** A single frame as produced by pkg/protocol.Frame on the server. */
export interface StreamFrame {
  /** 1-based, contiguous within one connection. */
  sequence: number;
  /** Server wall clock in Unix milliseconds at the moment the frame was built. */
  server_time: number;
  /** Number of payload bytes; the payload is omitted when this is zero. */
  payload_size: number;
  /** Fill bytes ('a' repeated); present only when payload_size > 0. */
  payload?: string;
}

export type Protocol = 'http-stream' | 'sse' | 'websocket';

/**
 * Canonical protocol order. Chart series indices and table order both follow it,
 * so a protocol that is not running leaves a gap rather than shifting the others.
 */
export const PROTOCOLS: readonly Protocol[] = ['http-stream', 'sse', 'websocket'];

/** Lifecycle of a single test run. */
export type ConnectionState =
  | 'idle'
  | 'connecting'
  | 'connected'
  | 'completed'
  | 'disconnected'
  | 'error';

export interface Limits {
  max_duration: number;
  min_duration: number;
  max_interval: number;
  min_interval: number;
  max_payload_size: number;
  max_concurrent_streams: number;
  write_timeout_ms: number;
}

export interface Defaults {
  duration: number;
  interval: number;
  payload_size: number;
}

export interface ConfigResponse {
  name: string;
  version: string;
  protocols: Protocol[];
  endpoints: Record<string, string>;
  limits: Limits;
  defaults: Defaults;
}

export interface InfoResponse {
  name: string;
  version: string;
  protocols: Protocol[];
  endpoints: Record<string, string>;
  uptime_seconds: number;
  active_streams: number;
  limits: Limits;
  defaults: Defaults;
}

export interface HealthResponse {
  status: string;
  version: string;
  uptime_seconds: number;
}

/** Mirrors internal/metrics.Snapshot, as returned by /api/metrics. */
export interface MetricsResponse {
  active_streams: number;
  streams_started: number;
  streams_finished: number;
  streams_cancelled: number;
  streams_errored: number;
  streams_rejected: number;
  frames_sent: number;
  bytes_sent: number;
}

/** The parameters of one test run. Durations are in seconds. */
export interface StreamParams {
  duration: number;
  interval: number;
  payload_size: number;
}

/**
 * A full snapshot of the client side measurements. Every field is derived from
 * frames that actually arrived over the socket; nothing is simulated.
 */
export interface MetricsSnapshot {
  state: ConnectionState;
  detail: string;
  elapsedMs: number;
  frames: number;
  /** Application bytes received, protocol framing excluded. */
  bytes: number;
  payloadBytes: number;
  avgThroughputBps: number;
  currentThroughputBps: number;
  minIntervalMs: number | null;
  avgIntervalMs: number | null;
  maxIntervalMs: number | null;
  /** Number of inter-arrival gaps that exceeded the stall threshold. */
  interruptions: number;
  /** The gap size that counts as an interruption for this run. */
  thresholdMs: number;
  /** Worst qualifying stall. */
  longestGapMs: number;
  totalGapMs: number;
  /** Worst inter-arrival time overall, qualifying or not. */
  maxGapMs: number;
  /** Wall clock independence: monotonic milliseconds to the first frame. */
  firstFrameLatencyMs: number | null;
  reconnects: number;
  sequenceGaps: number;
  missingFrames: number;
  outOfOrderFrames: number;
  integrityErrors: number;
}
