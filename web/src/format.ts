/** The share of a page's links that stay on the starting domain, or a dash when the page has none to score. */
export function formatRatio(ratio: number | null): string {
  return ratio === null ? '—' : ratio.toFixed(2);
}

export function formatTime(value: string | null): string {
  return value === null ? '—' : new Date(value).toLocaleString();
}
