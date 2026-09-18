/**
 * Table export.
 *
 * The console writes CSV rather than a binary workbook: the front end ships no
 * dependency beyond uPlot, and a UTF-8 CSV with a byte order mark opens straight
 * into Excel, Numbers and Sheets with the Chinese column headers intact.
 *
 * Both export shapes share one layout — a leading section column, a metric
 * column, then one column per protocol — so a single protocol's file is exactly
 * the matching column of the combined file.
 */

/** One label/value pair printed in the metadata block above the table. */
export interface ExportMetaRow {
  label: string;
  value: string;
}

/** One reading, with one value per protocol column. */
export interface ExportRow {
  label: string;
  values: string[];
}

/** A titled block of readings, matching one table on screen. */
export interface ExportSection {
  label: string;
  rows: ExportRow[];
}

export interface ExportInput {
  /** Heading of the leading section column. */
  groupHeading: string;
  /** Heading of the metric column. */
  metricHeading: string;
  /** Section label used for the metadata block above the table. */
  metaGroup: string;
  meta: ExportMetaRow[];
  /** One heading per value column; the number of columns is its length. */
  columns: string[];
  sections: ExportSection[];
  /**
   * Optional second table listing every retained frame. It gets its own heading
   * row and column set because one row per frame is a different shape from one
   * row per metric, and both cannot share a header.
   */
  detail?: ExportDetail;
}

/** The frame-by-frame table that follows the summary in the same file. */
export interface ExportDetail {
  /** Heading printed on its own line above the detail columns. */
  title: string;
  columns: string[];
  rows: string[][];
}

/**
 * Merges one section list per protocol into a single wide table.
 *
 * Every card builds the same sections in the same order, so the sections are
 * zipped by position; a protocol that has no reading in a slot contributes the
 * fallback rather than shifting the columns.
 */
export function mergeSections(
  perColumn: readonly (readonly ExportSection[])[],
  fallback: string,
): ExportSection[] {
  const template = perColumn[0] ?? [];
  return template.map((section, sectionIndex) => ({
    label: section.label,
    rows: section.rows.map((row, rowIndex) => ({
      label: row.label,
      values: perColumn.map(
        (column) => column[sectionIndex]?.rows[rowIndex]?.values[0] ?? fallback,
      ),
    })),
  }));
}

/** Renders the document as CSV text, ready to be written to a file. */
export function buildCSV(input: ExportInput): string {
  // The metadata block is padded to the width of the table so that every record
  // in the file has the same number of fields.
  const width = input.columns.length + 2;
  const padding = Array<string>(Math.max(0, width - 3)).fill('');

  const records: string[][] = [[input.groupHeading, input.metricHeading, ...input.columns]];

  for (const entry of input.meta) {
    records.push([input.metaGroup, entry.label, entry.value, ...padding]);
  }
  for (const section of input.sections) {
    for (const row of section.rows) {
      records.push([section.label, row.label, ...row.values]);
    }
  }

  if (input.detail) {
    // A blank record and a fresh heading separate the two tables, so the file
    // reads as one document rather than one mismatched sheet.
    records.push([]);
    records.push([input.detail.title]);
    records.push(input.detail.columns);
    for (const row of input.detail.rows) {
      records.push(row);
    }
  }

  const body = records.map((record) => record.map(escapeCell).join(',')).join('\r\n');
  // The BOM is what makes Excel read the file as UTF-8 instead of the local code
  // page, which is the difference between readable headers and mojibake.
  return `\uFEFF${body}\r\n`;
}

/** Quotes a field when it contains a delimiter, a quote or a line break. */
function escapeCell(value: string): string {
  return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

/** `YYYYMMDD-HHmmss` in local time, so file names sort chronologically. */
export function timestampSuffix(at: Date = new Date()): string {
  const pad = (value: number): string => String(value).padStart(2, '0');
  return (
    `${at.getFullYear()}${pad(at.getMonth() + 1)}${pad(at.getDate())}` +
    `-${pad(at.getHours())}${pad(at.getMinutes())}${pad(at.getSeconds())}`
  );
}

/** Builds the download name, e.g. `nsst-all-20260211-093000.csv`. */
export function exportFilename(kind: string, at: Date = new Date()): string {
  return `nsst-${kind}-${timestampSuffix(at)}.csv`;
}

/** Hands the CSV to the browser as a download. */
export function downloadCSV(filename: string, csv: string): void {
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
  // Revoked on the next turn of the loop, once the download has been handed to
  // the browser and the object URL is no longer needed.
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}
