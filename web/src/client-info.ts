/**
 * The panel at the top of the console that shows who is looking at it.
 *
 * Half of these facts are what this server saw on the request that loaded the
 * page (address, proxy chain, transport, user agent); the other half come from
 * the browser's own Navigator API and are never sent anywhere. Nothing here is
 * resolved against a third party, so the panel works on an isolated network.
 *
 * The panel can be hidden, and that choice is remembered across visits.
 */

import { fetchClient } from './api.js';
import type { ExportMetaRow } from './export.js';
import { t } from './i18n.js';
import type { ClientResponse } from './types.js';
import { requireElement } from './ui.js';

const STORAGE_KEY = 'nsst.client.hidden';

/** Display names for the proxy headers the server may have trusted. */
const SOURCE_HEADERS: Record<string, string> = {
  'cf-connecting-ip': 'CF-Connecting-IP',
  'true-client-ip': 'True-Client-IP',
  'x-real-ip': 'X-Real-IP',
  'x-forwarded-for': 'X-Forwarded-For',
};

interface NetworkInformationLike {
  effectiveType?: string;
  downlink?: number;
  rtt?: number;
  saveData?: boolean;
}

interface UserAgentDataLike {
  platform?: string;
  mobile?: boolean;
}

/**
 * Optional members of Navigator. Every one of them is missing in at least one
 * shipping browser, so they are read defensively rather than feature detected.
 */
interface ExtendedNavigator extends Navigator {
  connection?: NetworkInformationLike;
  deviceMemory?: number;
  userAgentData?: UserAgentDataLike;
}

/** A translated label plus the value it describes; also the CSV metadata rows. */
interface Fact {
  labelKey: string;
  labelParams?: Record<string, string | number>;
  value: string;
  /** Full text for values the cell has to shorten. */
  title?: string;
}

/** `—`, the same placeholder the metric tables use for an unknown reading. */
function na(): string {
  return t('value.na');
}

function readHidden(): boolean {
  try {
    return window.localStorage.getItem(STORAGE_KEY) === '1';
  } catch {
    // Private mode or a blocked storage API: show the panel.
    return false;
  }
}

function writeHidden(hidden: boolean): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, hidden ? '1' : '0');
  } catch {
    // Persisting is best effort only.
  }
}

function describeBrowser(userAgent: string): string {
  const browser = matchBrowser(userAgent);
  const platform = matchPlatform(userAgent);
  if (browser && platform) return `${browser} · ${platform}`;
  return browser || platform || na();
}

function matchBrowser(userAgent: string): string {
  // Order matters: Edge and Opera both carry a Chrome token, and Chrome carries
  // a Safari token.
  const patterns: [RegExp, string][] = [
    [/Edg(?:e|A|iOS)?\/([\d.]+)/, 'Edge'],
    [/OPR\/([\d.]+)/, 'Opera'],
    [/Firefox\/([\d.]+)/, 'Firefox'],
    [/(?:Chrome|CriOS)\/([\d.]+)/, 'Chrome'],
    [/Version\/([\d.]+).*Safari/, 'Safari'],
  ];
  for (const [pattern, name] of patterns) {
    const match = pattern.exec(userAgent);
    if (match) return `${name} ${match[1].split('.')[0]}`;
  }
  return '';
}

function matchPlatform(userAgent: string): string {
  if (/Windows NT 10\.0/.test(userAgent)) return 'Windows 10/11';
  if (/Windows NT 6\.3/.test(userAgent)) return 'Windows 8.1';
  if (/Windows NT 6\.1/.test(userAgent)) return 'Windows 7';
  if (/Windows/.test(userAgent)) return 'Windows';
  if (/Android/.test(userAgent)) return 'Android';
  if (/iPhone|iPod/.test(userAgent)) return 'iOS';
  if (/iPad/.test(userAgent)) return 'iPadOS';
  if (/Mac OS X/.test(userAgent)) return 'macOS';
  if (/CrOS/.test(userAgent)) return 'ChromeOS';
  if (/Linux/.test(userAgent)) return 'Linux';
  return '';
}

function describeTimezone(): string {
  const offsetMinutes = -new Date().getTimezoneOffset();
  const sign = offsetMinutes < 0 ? '-' : '+';
  const absolute = Math.abs(offsetMinutes);
  const pad = (value: number): string => String(value).padStart(2, '0');
  const offset = `UTC${sign}${pad(Math.floor(absolute / 60))}:${pad(absolute % 60)}`;

  let zone = '';
  try {
    zone = Intl.DateTimeFormat().resolvedOptions().timeZone ?? '';
  } catch {
    // Older engines may not implement resolvedOptions; the offset still works.
  }
  return zone ? `${zone} (${offset})` : offset;
}

function describeHardware(nav: ExtendedNavigator): string {
  const parts: string[] = [];
  const cores = nav.hardwareConcurrency;
  if (typeof cores === 'number' && cores > 0) {
    parts.push(t('client.coresValue', { count: cores }));
  }
  const memory = nav.deviceMemory;
  if (typeof memory === 'number' && memory > 0) {
    parts.push(t('client.memoryValue', { value: memory }));
  }
  return parts.length > 0 ? parts.join(' · ') : na();
}

function describeNetwork(nav: ExtendedNavigator): string {
  const connection = nav.connection;
  if (!connection) return na();
  const parts: string[] = [];
  if (connection.effectiveType) parts.push(connection.effectiveType);
  if (typeof connection.downlink === 'number') {
    parts.push(t('client.downlinkValue', { value: connection.downlink }));
  }
  if (typeof connection.rtt === 'number') {
    parts.push(t('client.rttValue', { value: connection.rtt }));
  }
  return parts.length > 0 ? parts.join(' · ') : na();
}

function headerName(source: string): string {
  return SOURCE_HEADERS[source] ?? source;
}

/**
 * Fetches what the server can see, merges it with what the browser knows, and
 * renders both. The panel never throws: a failed request leaves the
 * browser-side facts on screen with a note that the server did not answer.
 */
export class ClientInfoBar {
  private readonly fields: HTMLElement;
  private readonly toggle: HTMLButtonElement;
  private readonly root: HTMLElement;

  private facts: Fact[] = [];
  private server: ClientResponse | null = null;
  /** Server clock minus browser clock, from the last successful load. */
  private clockSkewMs: number | null = null;
  private failed = false;
  private hidden: boolean;

  constructor(root: HTMLElement) {
    this.root = root;
    this.fields = requireElement('client-fields');
    this.toggle = requireElement<HTMLButtonElement>('client-toggle');
    this.hidden = readHidden();

    this.toggle.addEventListener('click', () => this.setHidden(!this.hidden));
    this.rebuild();
    this.render();
  }

  /** Reads /api/client and repaints. Safe to call again on a new run. */
  async load(): Promise<void> {
    // The server samples its clock on the way in; averaging the two local stamps
    // around the request puts the estimate within half a round trip.
    const before = Date.now();
    try {
      const info = await fetchClient();
      const after = Date.now();
      this.server = info;
      this.clockSkewMs = info.server_time - (before + after) / 2;
      this.failed = false;
    } catch {
      this.server = null;
      this.clockSkewMs = null;
      this.failed = true;
    }
    this.rebuild();
    this.render();
  }

  /**
   * Rebuilds and repaints after a language switch.
   *
   * Values are composed from translated units ("16 cores", "150 ms RTT"), so a
   * switch has to rebuild the facts, not just re-render the labels.
   */
  renderLabels(): void {
    this.rebuild();
    this.render();
  }

  /** The same facts, as label/value pairs for the CSV metadata block. */
  metaRows(): ExportMetaRow[] {
    const rows = this.facts.map((fact) => ({
      label: t(fact.labelKey, fact.labelParams),
      value: fact.value,
    }));
    const userAgent = this.server?.user_agent || navigator.userAgent;
    if (userAgent) rows.push({ label: t('client.fullUserAgent'), value: userAgent });
    rows.push({ label: t('client.page'), value: window.location.href });
    return rows;
  }

  private rebuild(): void {
    const nav = navigator as ExtendedNavigator;
    const server = this.server;
    const userAgent = server?.user_agent || navigator.userAgent;

    const address = server
      ? server.proxy
        ? t('client.viaHeader', { header: headerName(server.ip_source) })
        : t('client.direct')
      : na();

    const transport = server
      ? `${server.proto} · ${server.tls ? (server.tls_version ?? 'TLS') : t('client.plain')}`
      : na();

    const languages = navigator.languages ?? [];
    const proxyChain = server?.forwarded_for ?? [];
    const title = (value: string | undefined): { title?: string } => (value ? { title: value } : {});

    this.facts = [
      { labelKey: 'client.ip', value: server?.ip || na() },
      {
        labelKey: 'client.ipSource',
        value: address,
        ...title(proxyChain.length > 0 ? proxyChain.join(' → ') : undefined),
      },
      { labelKey: 'client.remoteAddr', value: server?.remote_addr || na() },
      { labelKey: 'client.transport', value: transport },
      { labelKey: 'client.host', value: server?.host || na() },
      { labelKey: 'client.browser', value: describeBrowser(userAgent), ...title(userAgent) },
      {
        labelKey: 'client.language',
        value: navigator.language || na(),
        ...title(languages.length > 0 ? languages.join(', ') : undefined),
      },
      { labelKey: 'client.timezone', value: describeTimezone() },
      {
        labelKey: 'client.screen',
        value: `${screen.width}×${screen.height} @${window.devicePixelRatio}x`,
      },
      { labelKey: 'client.hardware', value: describeHardware(nav) },
      { labelKey: 'client.network', value: describeNetwork(nav) },
      {
        labelKey: 'client.clockSkew',
        value: this.skewLabel(),
        ...title(t('client.skewTitle')),
      },
    ];
  }

  private skewLabel(): string {
    if (this.clockSkewMs === null) return na();
    const rounded = Math.round(this.clockSkewMs);
    return `${rounded > 0 ? '+' : ''}${rounded} ${t('unit.ms')}`;
  }

  private setHidden(hidden: boolean): void {
    this.hidden = hidden;
    writeHidden(hidden);
    this.render();
  }

  private render(): void {
    this.root.classList.toggle('client-collapsed', this.hidden);

    this.toggle.textContent = this.hidden ? t('client.show') : t('client.hide');
    this.toggle.title = this.hidden ? t('client.showTitle') : t('client.hideTitle');

    const items: HTMLElement[] = this.facts.map((fact) => {
      const item = document.createElement('div');
      item.className = 'client-field';

      const label = document.createElement('span');
      label.className = 'client-label';
      label.textContent = t(fact.labelKey, fact.labelParams);

      const value = document.createElement('span');
      value.className = 'client-value mono';
      value.textContent = fact.value;
      // The cell shortens a long value rather than wrapping it, so the tooltip
      // carries the full text whether or not the value has one of its own.
      value.title = fact.title ?? fact.value;

      item.append(label, value);
      return item;
    });

    if (this.failed) {
      const note = document.createElement('p');
      note.className = 'client-error';
      note.textContent = t('client.unavailable');
      items.push(note);
    }

    this.fields.replaceChildren(...items);
  }
}
