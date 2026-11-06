/**
 * The question before a board joins the hive in place of a hornet (Console → «Вкатить плату в Улей»): it names that
 * hornet, because its old board loses access at once. Without a hornet chosen the typed code decides which one.
 */
export function joinQuestion(
  texts: { confirm: string; replaces: string; byCode: string },
  ssid: string,
  url: string,
  hornet: { name: string; deviceId: string } | null,
): string {
  const what = hornet ? texts.replaces.replace('{name}', hornet.name).replace('{id}', hornet.deviceId) : texts.byCode
  return `${texts.confirm.replace('{ssid}', ssid).replace('{url}', url)}\n\n${what}`
}
