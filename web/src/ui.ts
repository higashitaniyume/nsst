/** Small DOM helpers shared by the console views. */

/** Resolves a required element, failing loudly when the markup and the code disagree. */
export function requireElement<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) throw new Error(`missing element #${id}`);
  return element as T;
}

export function setText(element: HTMLElement | null, value: string): void {
  if (element) element.textContent = value;
}

/** Replaces `element`'s classes with `base` plus the modifiers given. */
export function setClass(element: HTMLElement | null, base: string, ...modifiers: string[]): void {
  if (!element) return;
  element.className = [base, ...modifiers].filter(Boolean).join(' ');
}

export interface NumericChoiceOptions {
  min: number;
  max: number;
  step?: number;
  /** Applied to a typed custom value before it is used. */
  normalize?: (value: number) => number;
  onChange: (value: number) => void;
}

/**
 * A button group of presets plus a free-form custom entry.
 *
 * Exactly one preset is highlighted at a time; when the current value does not
 * match any preset the custom input is shown and highlighted instead.
 */
export class NumericChoiceGroup {
  private readonly buttons: HTMLButtonElement[];
  private readonly customButton: HTMLButtonElement | null;
  private readonly input: HTMLInputElement | null;
  private value: number;

  constructor(
    root: HTMLElement,
    private readonly options: NumericChoiceOptions,
  ) {
    this.buttons = Array.from(root.querySelectorAll<HTMLButtonElement>('button[data-value]'));
    this.customButton = root.querySelector<HTMLButtonElement>('button[data-custom]');
    this.input = root.querySelector<HTMLInputElement>('input[data-custom-input]');
    this.value = options.min;

    for (const button of this.buttons) {
      button.addEventListener('click', () => {
        const raw = Number(button.dataset.value);
        if (!Number.isFinite(raw)) return;
        this.apply(raw);
      });
    }

    if (this.customButton && this.input) {
      this.customButton.addEventListener('click', () => {
        this.showCustom();
        this.input?.focus();
        this.input?.select();
      });
      this.input.addEventListener('change', () => this.commitCustom());
      this.input.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          this.commitCustom();
        }
      });
    }
  }

  get current(): number {
    return this.value;
  }

  /** Sets the value without notifying the listener. */
  set(value: number): void {
    this.value = value;
    this.paint();
  }

  /** Widens or narrows the accepted range, e.g. once the server limits are known. */
  setBounds(min: number, max: number): void {
    this.options.min = min;
    this.options.max = max;
  }

  /** Disables or re-enables every control in the group. */
  setDisabled(disabled: boolean): void {
    for (const button of this.buttons) button.disabled = disabled;
    if (this.customButton) this.customButton.disabled = disabled;
    if (this.input) this.input.disabled = disabled;
  }

  private apply(value: number): void {
    const clamped = this.clamp(value);
    this.value = clamped;
    this.paint();
    this.options.onChange(clamped);
  }

  private commitCustom(): void {
    if (!this.input) return;
    const raw = Number(this.input.value);
    if (!Number.isFinite(raw)) {
      this.input.value = String(this.value);
      return;
    }
    let next = this.clamp(raw);
    if (this.options.normalize) next = this.options.normalize(next);
    this.apply(next);
  }

  private clamp(value: number): number {
    const step = this.options.step ?? 1;
    const snapped = Math.round(value / step) * step;
    return Math.min(this.options.max, Math.max(this.options.min, snapped));
  }

  private showCustom(): void {
    if (!this.input) return;
    this.input.hidden = false;
    this.input.value = String(this.value);
    for (const button of this.buttons) button.classList.remove('is-active');
    this.customButton?.classList.add('is-active');
  }

  private paint(): void {
    const text = String(this.value);
    let matched = false;
    for (const button of this.buttons) {
      const active = button.dataset.value === text;
      button.classList.toggle('is-active', active);
      if (active) matched = true;
    }

    if (!this.input) return;
    if (matched) {
      this.input.hidden = true;
      this.input.value = text;
      this.customButton?.classList.remove('is-active');
    } else {
      this.input.hidden = false;
      this.input.value = text;
      this.customButton?.classList.add('is-active');
    }
  }
}

export type LogLevel = 'info' | 'success' | 'warn' | 'error';

/** A bounded, newest-last event log. */
export class EventLog {
  constructor(
    private readonly list: HTMLElement,
    private readonly limit = 250,
  ) {}

  add(level: LogLevel, message: string): void {
    const item = document.createElement('li');
    item.className = `log-entry log-${level}`;

    const time = document.createElement('span');
    time.className = 'log-time';
    time.textContent = new Date().toLocaleTimeString(undefined, { hour12: false });

    const text = document.createElement('span');
    text.className = 'log-text';
    text.textContent = message;

    item.append(time, text);
    this.list.append(item);

    while (this.list.childElementCount > this.limit) {
      this.list.firstElementChild?.remove();
    }
    this.list.scrollTop = this.list.scrollHeight;
  }

  clear(): void {
    this.list.replaceChildren();
  }
}
