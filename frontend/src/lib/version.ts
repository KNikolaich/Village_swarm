/**
 * What the sidebar shows: the hive's version (the api's), else the page's own build (VITE_HIVE_VERSION), else "dev".
 * stale: the page was built for another version than the hive runs (an old copy kept by the browser).
 */
export function brandLabel(api: string | null | undefined, page: string | null | undefined): { text: string; stale: boolean } {
  const a = api?.trim() || null
  const p = page?.trim() || null
  return { text: brandVersion(a ?? p ?? 'dev') ?? 'dev', stale: a !== null && p !== null && a !== p }
}

/** The hive's version beside its name: as Settings shows it, cut to fit the sidebar (the full one stays in the tooltip). */
export function brandVersion(version: string | null | undefined, max = 14): string | null {
  const v = version?.trim()
  if (!v) return null
  return v.length > max ? `${v.slice(0, max - 1)}…` : v
}
