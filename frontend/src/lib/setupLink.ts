// The address that fills in the settings form of a remote monitor's own Wi-Fi point (192.168.4.1). Shown as a QR code on
// the hive's page: the phone joins "VillageSwarm-xxxx", its camera reads the code, the form opens ready to save.

export type PortalSettings = { url: string; token: string; ssid?: string; pass?: string; intervalMin?: number }

export const portalAddress = 'http://192.168.4.1/'

export function portalLink(s: PortalSettings): string {
  const q = new URLSearchParams()
  if (s.ssid) q.set('ssid', s.ssid)
  if (s.pass) q.set('pass', s.pass)
  q.set('url', s.url.trim())
  q.set('token', s.token.trim())
  if (s.intervalMin) q.set('interval', String(s.intervalMin))
  return `${portalAddress}?${q.toString()}`
}
