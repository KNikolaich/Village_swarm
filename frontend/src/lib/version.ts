/** The hive's version beside its name: as Settings shows it, cut to fit the sidebar (the full one stays in the tooltip). */
export function brandVersion(version: string | null | undefined, max = 14): string | null {
  const v = version?.trim()
  if (!v) return null
  return v.length > max ? `${v.slice(0, max - 1)}…` : v
}
