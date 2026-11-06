// The address a monitor far away reaches the hive at: the one the person opens the hive with from outside (KeenDNS), not
// the address inside the house. Remembered once typed, so the next monitor needs no typing.

const KEY = 'hive.publicUrl'

const insideHouse = (hostname: string) =>
  hostname === 'localhost' || !hostname.includes('.') || hostname.endsWith('.local') || /^\d{1,3}(\.\d{1,3}){3}$/.test(hostname)

/** What was remembered, else this page's own address when it is a public name; empty when neither is known. */
export function publicHiveUrl(): string {
  try {
    const stored = localStorage.getItem(KEY)
    if (stored) return stored
  } catch { /* storage may be blocked */ }
  const { origin, hostname } = window.location
  return insideHouse(hostname) ? '' : origin
}

export function rememberHiveUrl(url: string) {
  try {
    localStorage.setItem(KEY, url.trim())
  } catch { /* storage may be blocked */ }
}
