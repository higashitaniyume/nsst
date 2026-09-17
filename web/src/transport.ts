import type { Protocol, StreamFrame } from './types.js';

/** Why a connection ended. */
export type CloseReason =
  /** The server finished the stream normally. */
  | 'completed'
  /** The connection went away before the requested duration elapsed. */
  | 'closed'
  /** The connection failed; the payload never became a valid stream. */
  | 'error';

/**
 * A translatable diagnostic. Transports report a message key plus its
 * placeholders rather than a finished string, so the console can render it in
 * whichever language the user picked.
 */
export interface Message {
  key: string;
  params?: Record<string, string | number>;
}

export function msg(key: string, params?: Record<string, string | number>): Message {
  return params ? { key, params } : { key };
}

export interface TransportCallbacks {
  /** The connection is established and the stream is live. */
  onOpen(): void;
  /** One decoded frame plus the application bytes it occupied. */
  onFrame(frame: StreamFrame, wireBytes: number): void;
  /** The connection ended. Never called as a result of {@link Transport.stop}. */
  onClose(reason: CloseReason, message: Message): void;
  /** A frame could not be decoded, or the transport reported a problem. */
  onError(message: Message): void;
}

/**
 * One live connection. Each implementation talks to the server with the native
 * browser API for its protocol; nothing here fakes or replays network traffic.
 */
export interface Transport {
  readonly protocol: Protocol;
  start(): void;
  stop(): void;
}

const utf8 = new TextEncoder();

/** Byte length of a string as UTF-8. */
export function utf8Length(value: string): number {
  return utf8.encode(value).length;
}

/** Shortens a payload for an error message. */
export function preview(value: string, max = 120): string {
  return value.length <= max ? value : `${value.slice(0, max)}…`;
}

/** Turns an unknown thrown value into a readable detail string. */
export function describeError(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (typeof error === 'string') return error;
  return String(error);
}

interface ErrorEnvelope {
  error?: string;
  message?: string;
}

/**
 * Reads the server's JSON error envelope out of a failed response so that the
 * console can show "invalid_parameter: interval must be at least 10
 * milliseconds" instead of a bare "HTTP 400".
 */
export async function describeHTTPFailure(response: Response): Promise<string> {
  try {
    const text = await response.text();
    if (text) {
      const parsed = JSON.parse(text) as ErrorEnvelope;
      if (parsed.message) {
        return parsed.error ? `${parsed.error}: ${parsed.message}` : parsed.message;
      }
      return preview(text);
    }
  } catch {
    // Fall through to the status line.
  }
  return response.statusText || 'request rejected';
}

/**
 * Shared bookkeeping so that every transport reports exactly one terminal event
 * and none of them report one after `stop` was requested.
 */
export class TerminalGuard {
  private stopped = false;
  private finished = false;

  constructor(private readonly callbacks: TransportCallbacks) {}

  get isStopped(): boolean {
    return this.stopped;
  }

  /** Marks the transport as intentionally stopped; suppresses the close event. */
  markStopped(): void {
    this.stopped = true;
  }

  /** Reports the terminal event exactly once. */
  finish(reason: CloseReason, message: Message): void {
    if (this.finished || this.stopped) return;
    this.finished = true;
    this.callbacks.onClose(reason, message);
  }

  error(message: Message): void {
    if (this.stopped) return;
    this.callbacks.onError(message);
  }
}
