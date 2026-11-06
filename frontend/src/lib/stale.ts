/** A reading older than this is shown dimmed. */
export const staleAfterMs = 3 * 3_600_000

/**
 * Whether a reading has gone quiet. Only the age of the reading decides: a hornet's online status can lag behind (a
 * hornet that sleeps between reports, or one that reports through a bridge), and a fresh value means it is alive.
 */
export function isStale(ts: string | null | undefined, now = Date.now()): boolean {
  return !ts || now - new Date(ts).getTime() > staleAfterMs
}
