/**
 * The tab and the installed app are named after the hive. The signed-in shell knows the alias from /api/cluster, but the sign-in
 * page cannot ask for it, so the title is taken from the manifest the api builds (open to everyone, short_name is the alias).
 */
export function titleFromManifest(manifest: unknown): string | null {
  const name = (manifest as { short_name?: unknown } | null)?.short_name
  return typeof name === 'string' && name.trim() ? name.trim() : null
}

export async function loadHiveTitle(): Promise<string | null> {
  try {
    const response = await fetch('/manifest.webmanifest', { cache: 'no-store' })
    return response.ok ? titleFromManifest(await response.json()) : null
  } catch {
    return null
  }
}
