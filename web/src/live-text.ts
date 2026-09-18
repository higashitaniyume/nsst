import { t } from './i18n.js';

/**
 * How much of the stream is kept in the DOM.
 *
 * The block exists to show that data is still arriving, so it only has to hold
 * the tail: a soak test would otherwise grow the page without bound. The buffer
 * is allowed to reach twice this length before the oldest half is dropped, which
 * keeps the number of rewrites proportional to the text rather than to the frame
 * rate.
 */
const KEEP_CHARS = 6000;

/** Distance from the bottom, in pixels, that still counts as "following". */
const FOLLOW_SLACK_PX = 24;

/**
 * Shows the bytes one protocol's stream actually sent, appended in arrival order.
 *
 * The text is written exactly as it was decoded from the wire: nothing here
 * paces, animates or reorders it. That is deliberate — a stall in the stream has
 * to look like a stall on screen, which a typewriter effect would hide.
 *
 * One instance belongs to one protocol, and it renders into the slot inside that
 * protocol's own card, so the raw text sits next to the readings and charts that
 * describe the same stream. The slot is coloured by the card's data-proto
 * attribute, so nothing here needs to know about protocol colours.
 */
export class LiveText {
  private readonly head: HTMLElement;
  private readonly body: HTMLPreElement;
  private buffer = '';

  constructor(slot: HTMLElement) {
    this.head = document.createElement('div');
    this.head.className = 'live-head';
    this.head.textContent = t('live.title');
    this.head.title = t('live.hint');

    this.body = document.createElement('pre');
    this.body.className = 'live-body';
    this.body.textContent = t('live.idle');

    slot.append(this.head, this.body);
  }

  /** Appends one frame's payload, in the order the frame arrived. */
  append(chunk: string): void {
    if (!chunk) return;

    this.buffer += chunk;
    if (this.buffer.length > KEEP_CHARS * 2) {
      this.buffer = this.buffer.slice(-KEEP_CHARS);
    }

    // Follow the tail only when the reader is already at the bottom, so that
    // scrolling back to read something does not fight the stream.
    const following =
      this.body.scrollHeight - this.body.scrollTop - this.body.clientHeight <= FOLLOW_SLACK_PX;
    this.body.textContent = this.buffer;
    if (following) this.body.scrollTop = this.body.scrollHeight;
  }

  /** Drops everything and goes back to the waiting state. */
  clear(): void {
    this.buffer = '';
    this.body.textContent = t('live.idle');
  }

  /** Re-renders the heading after a language switch. */
  renderLabels(): void {
    this.head.textContent = t('live.title');
    this.head.title = t('live.hint');
    // Only the untouched placeholder is this block's own text; anything else is
    // server data and must not be translated or thrown away.
    if (!this.buffer) this.body.textContent = t('live.idle');
  }
}
