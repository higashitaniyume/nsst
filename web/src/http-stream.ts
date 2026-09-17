import type { StreamFrame } from './types.js';
import {
  describeError,
  describeHTTPFailure,
  msg,
  preview,
  TerminalGuard,
  utf8Length,
  type Transport,
  type TransportCallbacks,
} from './transport.js';

export const CONTENT_TYPE = 'application/x-ndjson';

/**
 * HTTP streaming client: a long lived chunked GET response carrying one
 * newline delimited JSON frame per write.
 *
 * `fetch` is used rather than `EventSource` because the NDJSON body has to be
 * consumed with full control over chunk boundaries; the response is read through
 * a `ReadableStream` so that every frame is surfaced as soon as the server
 * flushes it, without waiting for the response to end.
 */
export function createHTTPStreamTransport(url: string, callbacks: TransportCallbacks): Transport {
  const guard = new TerminalGuard(callbacks);
  const controller = new AbortController();

  const handleLine = (raw: string): void => {
    if (raw.length === 0) return;
    const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;
    if (line.length === 0) return;

    let frame: StreamFrame;
    try {
      frame = JSON.parse(line) as StreamFrame;
    } catch {
      guard.error(msg('err.malformedNDJSON', { detail: preview(line) }));
      return;
    }
    if (typeof frame.sequence !== 'number') {
      guard.error(msg('err.noSequence', { detail: preview(line) }));
      return;
    }
    // One newline delimiter per frame, so per-frame attribution is exact.
    callbacks.onFrame(frame, utf8Length(raw) + 1);
  };

  const run = async (): Promise<void> => {
    let response: Response;
    try {
      response = await fetch(url, {
        method: 'GET',
        headers: { Accept: CONTENT_TYPE },
        cache: 'no-store',
        signal: controller.signal,
      });
    } catch (error) {
      if (guard.isStopped) return;
      guard.finish('error', msg('err.network', { detail: describeError(error) }));
      return;
    }

    if (!response.ok) {
      const detail = await describeHTTPFailure(response);
      guard.finish('error', msg('err.httpStatus', { status: response.status, detail }));
      return;
    }
    if (!response.body) {
      guard.finish('error', msg('err.noBody'));
      return;
    }

    callbacks.onOpen();

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');
    let buffer = '';

    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        let newline = buffer.indexOf('\n');
        while (newline >= 0) {
          const raw = buffer.slice(0, newline);
          buffer = buffer.slice(newline + 1);
          handleLine(raw);
          newline = buffer.indexOf('\n');
        }
      }
      // Flush any bytes the decoder was still holding.
      buffer += decoder.decode();
      const tail = buffer.endsWith('\r') ? buffer.slice(0, -1) : buffer;
      if (tail.length > 0) {
        guard.error(msg('err.truncated', { detail: preview(tail) }));
      }
    } catch (error) {
      if (guard.isStopped) return;
      // A body that ends mid-stream surfaces here as a network error.
      guard.finish('closed', msg('err.bodyEnded', { detail: describeError(error) }));
      return;
    }

    guard.finish('completed', msg('close.completed'));
  };

  return {
    protocol: 'http-stream',
    start(): void {
      void run();
    },
    stop(): void {
      guard.markStopped();
      controller.abort();
    },
  };
}
