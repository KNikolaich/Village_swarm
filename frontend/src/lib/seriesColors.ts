/**
 * Colours for lines when a chart has several; the first is the single-line amber used everywhere else.
 * `band` is the soft min-max area drawn under a line when it is the only one.
 */
export const seriesColors = [
  { stroke: 'stroke-amber-400', fill: 'fill-amber-400', bg: 'bg-amber-400', band: 'fill-amber-400/15' },
  { stroke: 'stroke-sky-400', fill: 'fill-sky-400', bg: 'bg-sky-400', band: 'fill-sky-400/15' },
  { stroke: 'stroke-emerald-400', fill: 'fill-emerald-400', bg: 'bg-emerald-400', band: 'fill-emerald-400/15' },
  { stroke: 'stroke-rose-400', fill: 'fill-rose-400', bg: 'bg-rose-400', band: 'fill-rose-400/15' },
  { stroke: 'stroke-violet-400', fill: 'fill-violet-400', bg: 'bg-violet-400', band: 'fill-violet-400/15' },
]

/**
 * One colour per hornet across all the charts of a zone: a hornet keeps its colour whether it is the first line on
 * the temperature chart or the only one on the humidity chart, so the legends agree.
 */
export function colorsByDevice(deviceIds: readonly string[]) {
  return (deviceId: string) => seriesColors[Math.max(0, deviceIds.indexOf(deviceId)) % seriesColors.length]
}
