import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, X } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useDevices, useLatestTelemetry } from '@/api/queries'
import { HelpTip } from '@/components/HelpTip'
import { Button } from '@/components/ui/button'
import { Input, Select } from '@/components/ui/primitives'
import { metricLabel, t } from '@/i18n'
import { type Align, clampToScreen, labelHeight, maxAuto, maxPlaced, pixelHeight, type Screen, screenText, stackedPlaces, textAnchor } from '@/lib/display'

interface Row {
  /** The device whose reading is shown; '' is this hornet. */
  device: string
  metric: string
  label: string
  x: number
  y: number
  size: number
  align: Align
}

/** A board the hive does not know the screen of: the 0.42" OLED, the only one so far. */
const fallbackScreen: Screen = { width: 72, height: 40 }

function FieldLabel({ text, help }: { text: string; help: string }) {
  return <span className="flex items-center gap-1 text-xs text-neutral-400">{text}<HelpTip text={help} /></span>
}

/**
 * What the screen will look like, drawn at its own pixels and scaled up. Placed rows can be dragged to their spot; the
 * fonts are close to the screen's, not the same, so the picture is a guide.
 */
function ScreenPreview({ screen, rows, texts, onMove, editable }: {
  screen: Screen; rows: Row[]; texts: string[]; onMove: (i: number, x: number, y: number) => void; editable: boolean
}) {
  const svg = useRef<SVGSVGElement>(null)
  const drag = useRef<{ i: number; dx: number; dy: number } | null>(null)

  function point(e: ReactPointerEvent) {
    const box = svg.current!.getBoundingClientRect()
    return { x: ((e.clientX - box.left) / box.width) * screen.width, y: ((e.clientY - box.top) / box.height) * screen.height }
  }

  return (
    <svg ref={svg} viewBox={`0 0 ${screen.width} ${screen.height}`} role="img" aria-label={t('display.preview')}
      className="w-full max-w-72 touch-none select-none rounded border border-neutral-700 bg-black"
      style={{ aspectRatio: `${screen.width} / ${screen.height}` }}
      onPointerMove={(e) => {
        if (!drag.current) return
        const p = point(e)
        const { x, y } = clampToScreen(p.x - drag.current.dx, p.y - drag.current.dy, screen)
        onMove(drag.current.i, x, y)
      }}
      onPointerUp={() => { drag.current = null }}
      onPointerLeave={() => { drag.current = null }}>
      {rows.map((row, i) => {
        const valueTop = row.y + (row.label ? labelHeight : 0)
        const height = pixelHeight(row.size, screen.height)
        return (
          <g key={i} className={editable ? 'cursor-move' : undefined}
            onPointerDown={(e) => {
              if (!editable) return
              const p = point(e)
              drag.current = { i, dx: p.x - row.x, dy: p.y - row.y }
              svg.current?.setPointerCapture(e.pointerId)
            }}>
            {row.label && (
              <text x={row.x} y={row.y} dominantBaseline="hanging" textAnchor={textAnchor(row.align)} fontSize={7} fill="#fff" fontFamily="monospace">{row.label}</text>
            )}
            {/* digits take about 0.72 of the font size from the top */}
            <text x={row.x} y={valueTop} dominantBaseline="hanging" textAnchor={textAnchor(row.align)} fontSize={height / 0.72} fill="#fff" fontFamily="sans-serif">{texts[i]}</text>
          </g>
        )
      })}
    </svg>
  )
}

/**
 * The screen of a monitor or a hornet with a screen (0.42" OLED): which readings it shows, from this device or from another
 * one (a thermometer in the yard, a room, ...), laid out by the screen itself or placed by hand: x, y, a size 1-10 and an
 * alignment per row. The hive turns the choice into ready texts and sends them to the screen.
 */
export function DisplayPanel({ deviceId, editable }: { deviceId: string; editable: boolean }) {
  const qc = useQueryClient()
  const key = ['display', deviceId]
  const current = useQuery({ queryKey: key, queryFn: () => call(api.GET('/api/devices/{deviceId}/display', { params: { path: { deviceId } } })) })
  const devices = useDevices()
  const latest = useLatestTelemetry()
  const [rows, setRows] = useState<Row[]>([])
  const [placed, setPlaced] = useState(false)
  const screen: Screen = current.data?.screen ?? fallbackScreen
  useEffect(() => {
    if (!current.data) return
    setRows(current.data.items.map((i) => ({
      device: i.device ?? '', metric: i.metric, label: i.label ?? '',
      x: i.x ?? 0, y: i.y ?? 0, size: i.size ?? 3, align: (i.align as Align | null) ?? 'left',
    })))
    setPlaced(current.data.items.some((i) => i.size != null))
  }, [current.data])

  // The readings each device has had lately: a screen can only show what the hive has seen.
  const metricsOf = useMemo(() => {
    const map = new Map<string, string[]>()
    for (const v of latest.data ?? []) map.set(v.deviceId, [...(map.get(v.deviceId) ?? []), v.metric])
    return (id: string) => (map.get(id) ?? []).slice().sort()
  }, [latest.data])
  const valueOf = (id: string, metric: string) => (latest.data ?? []).find((v) => v.deviceId === id && v.metric === metric)?.value
  const nameOf = (id: string) => (devices.data ?? []).find((d) => d.deviceId === id)?.name ?? id
  const sources = (devices.data ?? []).filter((d) => d.deviceId !== deviceId && metricsOf(d.deviceId).length > 0)
  const maxRows = placed ? maxPlaced : maxAuto

  function setRow(i: number, patch: Partial<Row>) {
    setRows((all) => all.map((r, k) => {
      if (k !== i) return r
      const next = { ...r, ...patch }
      // another device: the first reading it has, when the old choice does not exist there
      if (patch.device !== undefined && !metricsOf(patch.device || deviceId).includes(next.metric)) next.metric = metricsOf(patch.device || deviceId)[0] ?? ''
      return next
    }))
  }

  function togglePlaced(on: boolean) {
    setPlaced(on)
    // switching on: the rows one under another, ready to be moved; switching off: the screen lays them out again
    if (on) setRows((all) => all.map((r, i) => ({ ...r, ...stackedPlaces(all.length, screen)[i], align: 'left' })))
    else setRows((all) => all.slice(0, maxAuto))
  }

  function addRow() {
    setRows((all) => [...all, {
      device: '', metric: metricsOf(deviceId)[0] ?? '', label: '',
      x: 0, y: Math.min(screen.height - 1, all.length * 10), size: 3, align: 'left',
    }])
  }

  const complete = rows.every((r) => r.metric !== '')
  const texts = rows.map((r) => screenText(r.metric, valueOf(r.device || deviceId, r.metric)))
  const number = (v: string, min: number, max: number) => Math.min(max, Math.max(min, Math.round(Number(v) || 0)))

  async function save() {
    const items = rows.map((r) => ({
      device: r.device || null, metric: r.metric, label: r.label.trim() || null,
      ...(placed ? { x: r.x, y: r.y, size: r.size, align: r.align === 'left' ? null : r.align } : {}),
    }))
    const { response } = await api.PUT('/api/devices/{deviceId}/display', { params: { path: { deviceId } }, body: { items } })
    if (!response.ok) {
      toast.error(t('errors.generic'))
      return
    }
    toast.success(t('display.saved'))
    await qc.invalidateQueries({ queryKey: key })
  }

  return (
    <div className="space-y-3 text-sm">
      <p className="text-xs text-neutral-500">{t('display.hint')}</p>
      <label className="flex items-center gap-2 text-xs text-neutral-300">
        <input type="checkbox" checked={placed} disabled={!editable} onChange={(e) => togglePlaced(e.target.checked)} />
        {t('display.placed')}
        <HelpTip text={t('display.placedHelp').replace('{w}', String(screen.width)).replace('{h}', String(screen.height))} />
      </label>
      {placed && rows.length > 0 && (
        <div className="space-y-1">
          <ScreenPreview screen={screen} rows={rows} texts={texts} editable={editable} onMove={(i, x, y) => setRow(i, { x, y })} />
          <p className="text-xs text-neutral-500">{t('display.previewHint').replace('{w}', String(screen.width)).replace('{h}', String(screen.height))}</p>
        </div>
      )}
      {rows.length === 0 && <p className="text-xs text-neutral-400">{t('display.own')}</p>}
      {rows.map((row, i) => {
        const metrics = metricsOf(row.device || deviceId)
        return (
          <div key={i} className="space-y-2 rounded-lg border border-neutral-800 p-2">
            <div className="grid grid-cols-[1fr_1fr_auto] items-end gap-2 sm:grid-cols-[1fr_1fr_1fr_auto]">
              <label className="space-y-1">
                <span className="text-xs text-neutral-400">{t('display.source')}</span>
                <Select value={row.device} disabled={!editable} onChange={(e) => setRow(i, { device: e.target.value })}>
                  <option value="">{t('display.thisHornet')}</option>
                  {sources.map((d) => <option key={d.deviceId} value={d.deviceId}>{d.name}</option>)}
                  {row.device && !sources.some((d) => d.deviceId === row.device) && <option value={row.device}>{nameOf(row.device)}</option>}
                </Select>
              </label>
              <label className="space-y-1">
                <span className="text-xs text-neutral-400">{t('display.data')}</span>
                <Select value={row.metric} disabled={!editable} onChange={(e) => setRow(i, { metric: e.target.value })}>
                  {row.metric && !metrics.includes(row.metric) && <option value={row.metric}>{metricLabel(row.metric)}</option>}
                  {metrics.map((m) => <option key={m} value={m}>{metricLabel(m)}</option>)}
                </Select>
              </label>
              <label className="col-span-2 space-y-1 sm:col-span-1">
                <FieldLabel text={t('display.label')} help={t(placed ? 'display.labelPlacedHelp' : 'display.labelHelp')} />
                <Input value={row.label} maxLength={16} disabled={!editable} placeholder={!placed && row.device ? nameOf(row.device) : ''} onChange={(e) => setRow(i, { label: e.target.value })} />
              </label>
              <Button variant="ghost" size="icon" className="size-9 shrink-0" disabled={!editable} title={t('display.remove')} aria-label={t('display.remove')}
                onClick={() => setRows((all) => all.filter((_, k) => k !== i))}><X /></Button>
            </div>
            {placed && (
              <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
                <label className="space-y-1">
                  <FieldLabel text="X" help={t('display.xHelp').replace('{max}', String(screen.width - 1))} />
                  <Input type="number" min={0} max={screen.width - 1} value={row.x} disabled={!editable}
                    onChange={(e) => setRow(i, { x: number(e.target.value, 0, screen.width - 1) })} />
                </label>
                <label className="space-y-1">
                  <FieldLabel text="Y" help={t('display.yHelp').replace('{max}', String(screen.height - 1))} />
                  <Input type="number" min={0} max={screen.height - 1} value={row.y} disabled={!editable}
                    onChange={(e) => setRow(i, { y: number(e.target.value, 0, screen.height - 1) })} />
                </label>
                <label className="space-y-1">
                  <FieldLabel text={t('display.size')} help={t('display.sizeHelp').replace('{px}', String(pixelHeight(5, screen.height)))} />
                  <Input type="number" min={1} max={10} value={row.size} disabled={!editable}
                    onChange={(e) => setRow(i, { size: number(e.target.value, 1, 10) })} />
                </label>
                <label className="space-y-1">
                  <FieldLabel text={t('display.align')} help={t('display.alignHelp')} />
                  <Select value={row.align} disabled={!editable} onChange={(e) => setRow(i, { align: e.target.value as Align })}>
                    <option value="left">{t('display.alignLeft')}</option>
                    <option value="center">{t('display.alignCenter')}</option>
                    <option value="right">{t('display.alignRight')}</option>
                  </Select>
                </label>
              </div>
            )}
          </div>
        )
      })}
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" disabled={!editable || rows.length >= maxRows} onClick={addRow}><Plus />{t('display.add')}</Button>
        <Button disabled={!editable || !complete} onClick={() => void save()}>{t('display.save')}</Button>
      </div>
      {!placed && <p className="text-xs text-neutral-500">{t('display.layout')}</p>}
    </div>
  )
}
