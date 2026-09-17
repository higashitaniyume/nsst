import type { StreamFrame } from './types.js';
import {
  msg,
  preview,
  TerminalGuard,
  utf8Length,
  type Transport,
  type TransportCallbacks,
} from './transport.js';

/** The server names its events, so the generic `message` listener is not used. */
export const EVENT_NAME = 'data';

/**
 * Server-Sent Events client built on the native `EventSource`.
 *
 * The browser's own automatic reconnection is deliberately suppressed: the run
 * controller owns the retry policy so that HTTP streaming, SSE and WebSocket all
 * behave identically, and so that a stream that simply ran out of time is not
 * silently restarted forever.
 */
export function createSSETransport(url: string, callbacks: TransportCallbacks): Transport {
  const guard = new TerminalGuard(callbacks);
  let source: EventSource | null = null;
  let opened = false;

  const closeSource = (): void => {
    if (source) {
      source.close();
      source = null;
    }
  };

  const onData = (event: MessageEvent<string>): void => {
    const data = typeof event.data === 'string' ? event.data : String(event.data);
    let frame: StreamFrame;
    try {
      frame = JSON.parse(data) as StreamFrame;
    } catch {
      guard.error(msg('err.malformedSSE', { detail: preview(data) }));
      return;
    }
    if (typeof frame.sequence !== 'number') {
      guard.error(msg('err.noSequence', { detail: preview(data) }));
      return;
    }
    // Reconstruct the exact event bytes the server wrote on the wire:
    //   id: <n>\n event: data\n data: <json>\n \n
    const id = event.lastEventId ?? '';
    const wireBytes = utf8Length(
      `id: ${id}\nevent: ${EVENT_NAME}\ndata: ${data}\n\n`,
    );
    callbacks.onFrame(frame, wireBytes);
  };

  const onError = (): void => {
    if (guard.isStopped) return;
    const state = source ? source.readyState : EventSource.CLOSED;

    if (!opened && state === EventSource.CLOSED) {
      guard.finish('error', msg('err.sseNotEstablished'));
      return;
    }
    if (state === EventSource.CLOSED) {
      guard.finish('closed', msg('err.sseClosed'));
      return;
    }
    // readyState is CONNECTING: the browser is about to retry on its own. Close
    // it and hand control back to the run controller, which decides whether this
    // was a drop worth recovering from or simply the end of the stream.
    closeSource();
    guard.finish('closed', msg('err.sseEnded'));
  };

  return {
    protocol: 'sse',
    start(): void {
      const next = new EventSource(url);
      source = next;
      next.onopen = () => {
        if (guard.isStopped) return;
        if (!opened) {
          opened = true;
          callbacks.onOpen();
        }
      };
      next.addEventListener(EVENT_NAME, onData as EventListener);
      next.onerror = onError;
    },
    stop(): void {
      guard.markStopped();
      closeSource();
    },
  };
}
