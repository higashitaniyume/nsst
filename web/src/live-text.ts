import { t } from './i18n.js';
import { PROTOCOL_COLORS } from './views.js';
import type { Protocol } from './types.js';

/** Canonical display order, matching the charts and the cards. */
const PROTOCOLS: Protocol[] = ['http-stream', 'sse', 'websocket'];

/**
 * How much of each stream is kept in the DOM.
 *
 * The panel exists to show that data is still arriving, so it only has to hold
 * the tail: a soak test would otherwise grow the page without bound. The buffer
 * is allowed to reach twice this length before the oldest half is dropped, which
 * keeps the number of rewrites proportional to the text rather than to the frame
 * rate.
 */
const KEEP_CHARS = 6000;

/** Distance from the bottom, in pixels, that still counts as "following". */
const FOLLOW_SLACK_PX = 24;

interface Slot {
  head: HTMLElement;
  body: HTMLPreElement;
}

/**
 * Shows the bytes the server actually sent, appended in arrival order.
 *
 * The text is written exactly as it was decoded from the wire: nothing here
 * paces, animates or reorders it. That is deliberate — a stall in the stream has
 * to look like a stall on screen, which a typewriter effect would hide.
 */
export class LiveTextPanel {
  private readonly slots = new Map<Protocol, Slot>();
  private readonly buffers = new Map<Protocol, string>();

  constructor(root: HTMLElement) {
    const grid = document.createElement('div');
    grid.className = 'live-grid';

    for (const protocol of PROTOCOLS) {
      const column = document.createElement('div');
      column.className = 'live-column';
      column.dataset.proto = protocol;

      const head = document.createElement('div');
      head.className = 'live-head';
      head.style.color = PROTOCOL_COLORS[protocol];
      head.textContent = t(`proto.${protocol}`);

      const body = document.createElement('pre');
      body.className = 'live-body';
      body.textContent = t('live.idle');

      column.append(head, body);
      grid.append(column);

      this.slots.set(protocol, { head, body });
      this.buffers.set(protocol, '');
    }

    root.append(grid);
  }

  /** Appends one frame's payload, in the order the frame arrived. */
  append(protocol: Protocol, chunk: string): void {
    if (!chunk) return;
    const slot = this.slots.get(protocol);
    if (!slot) return;

    let buffer = (this.buffers.get(protocol) ?? '') + chunk;
    if (buffer.length > KEEP_CHARS * 2) {
      buffer = buffer.slice(-KEEP_CHARS);
    }
    this.buffers.set(protocol, buffer);

    // Follow the tail only when the reader is already at the bottom, so that
    // scrolling back to read something does not fight the stream.
    const following =
      slot.body.scrollHeight - slot.body.scrollTop - slot.body.clientHeight <= FOLLOW_SLACK_PX;
    slot.body.textContent = buffer;
    if (following) slot.body.scrollTop = slot.body.scrollHeight;
  }

  /** Drops everything and goes back to the waiting state. */
  clear(): void {
    for (const protocol of PROTOCOLS) {
      this.buffers.set(protocol, '');
      const slot = this.slots.get(protocol);
      if (slot) slot.body.textContent = t('live.idle');
    }
  }

  /** Re-renders the headings after a language switch. */
  renderLabels(): void {
    for (const protocol of PROTOCOLS) {
      const slot = this.slots.get(protocol);
      if (!slot) continue;
      slot.head.textContent = t(`proto.${protocol}`);
      // Only the untouched placeholder is the panel's own text; anything else is
      // server data and must not be translated or thrown away.
      if (!this.buffers.get(protocol)) slot.body.textContent = t('live.idle');
    }
  }
}
