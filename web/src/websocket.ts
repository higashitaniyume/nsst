import type { StreamFrame } from './types.js';
import {
  msg,
  preview,
  TerminalGuard,
  utf8Length,
  type Transport,
  type TransportCallbacks,
} from './transport.js';

/** Close code the server uses when the requested duration elapsed. */
export const CLOSE_NORMAL = 1000;

/**
 * WebSocket client built on the native `WebSocket`.
 *
 * The server never expects a message from the client; the connection is closed
 * with a normal close frame once the requested duration has elapsed, which is
 * what `event.wasClean` and the close code report back here.
 */
export function createWebSocketTransport(url: string, callbacks: TransportCallbacks): Transport {
  const guard = new TerminalGuard(callbacks);
  let socket: WebSocket | null = null;
  let opened = false;

  const onMessage = (event: MessageEvent<unknown>): void => {
    const data = event.data;
    if (typeof data !== 'string') {
      guard.error(msg('err.binaryFrame'));
      return;
    }
    let frame: StreamFrame;
    try {
      frame = JSON.parse(data) as StreamFrame;
    } catch {
      guard.error(msg('err.malformedFrame', { detail: preview(data) }));
      return;
    }
    if (typeof frame.sequence !== 'number') {
      guard.error(msg('err.noSequence', { detail: preview(data) }));
      return;
    }
    callbacks.onFrame(frame, utf8Length(data));
  };

  const onClose = (event: CloseEvent): void => {
    if (guard.isStopped) return;
    if (!opened) {
      guard.finish('error', msg('err.wsHandshake', { code: event.code }));
      return;
    }
    if (event.wasClean) {
      guard.finish('closed', msg('err.wsClosed', {
        code: event.code,
        detail: event.reason ? ` (${event.reason})` : '',
      }));
      return;
    }
    guard.finish('error', msg('err.wsLost', { code: event.code }));
  };

  return {
    protocol: 'websocket',
    start(): void {
      const next = new WebSocket(url);
      socket = next;
      next.binaryType = 'arraybuffer';
      next.onopen = () => {
        if (guard.isStopped) return;
        opened = true;
        callbacks.onOpen();
      };
      next.onmessage = onMessage;
      next.onclose = onClose;
      next.onerror = () => {
        // The close event always follows and carries the useful detail.
      };
    },
    stop(): void {
      guard.markStopped();
      const current = socket;
      socket = null;
      if (!current) return;
      if (current.readyState === WebSocket.OPEN || current.readyState === WebSocket.CONNECTING) {
        // A clean close frame, so the server releases its stream slot at once.
        current.close(CLOSE_NORMAL, 'stopped by client');
      }
    },
  };
}
