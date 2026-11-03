/** The hive's address for a hornet: "192.168.10.4" is taken as http://192.168.10.4, a trailing slash dropped. */
export function hiveUrl(url: string): string {
  const v = url.trim().replace(/\/+$/, '')
  if (!v) return v
  return /^https?:\/\//i.test(v) ? v : `http://${v}`
}
