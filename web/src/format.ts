/** Presentation helpers. Pure functions, no DOM and no state. */

/** Formats a byte count with a binary unit. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes)) return '—';
  const abs = Math.abs(bytes);
  if (abs < 1024) return `${bytes.toFixed(0)} B`;
  if (abs < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  if (abs < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(2)} MiB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GiB`;
}

/** Formats a byte-per-second rate. */
export function formatRate(bytesPerSecond: number): string {
  if (!Number.isFinite(bytesPerSecond) || bytesPerSecond <= 0) return '0 B/s';
  return `${formatBytes(bytesPerSecond)}/s`;
}

/** Formats a duration in milliseconds with a sensible unit. */
export function formatMilliseconds(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms)) return '—';
  if (ms < 1000) return `${ms.toFixed(1)} ms`;
  return `${(ms / 1000).toFixed(2)} s`;
}

/** Formats an elapsed duration as mm:ss. */
export function formatClock(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
}

/** Formats a duration in seconds for display, e.g. 60 -> "1 min". */
export function formatSeconds(seconds: number): string {
  if (seconds < 60) return `${seconds} s`;
  if (seconds % 60 === 0) {
    const minutes = seconds / 60;
    if (minutes < 60) return `${minutes} min`;
    const hours = minutes / 60;
    return Number.isInteger(hours) ? `${hours} h` : `${hours.toFixed(1)} h`;
  }
  return `${seconds} s`;
}

/** Compact axis formatter for interval charts. */
export function formatAxisMs(ms: number): string {
  if (Math.abs(ms) >= 1000) return `${(ms / 1000).toFixed(1)}s`;
  return `${Math.round(ms)}ms`;
}

/** Compact axis formatter for throughput charts. */
export function formatAxisRate(bytesPerSecond: number): string {
  const abs = Math.abs(bytesPerSecond);
  if (abs >= 1024 * 1024) return `${(bytesPerSecond / (1024 * 1024)).toFixed(1)}M`;
  if (abs >= 1024) return `${(bytesPerSecond / 1024).toFixed(0)}K`;
  return `${Math.round(bytesPerSecond)}`;
}
