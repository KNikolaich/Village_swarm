import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ImagePlus, Plus, Trash2, Type, Upload, X } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useDevices, useLatestTelemetry } from '@/api/queries'
import { HelpTip } from '@/components/HelpTip'
import { Button } from '@/components/ui/button'
import { Input, Select } from '@/components/ui/primitives'
import { metricLabel, t } from '@/i18n'
import {
  type Align, backgroundSize, clampPx, clampToScreen, fontBadge, type FontInfo, labelHeight, maxAuto, maxPlaced, maxPx, minPx, pixelHeight,
  rotations, type Screen, screenText, stackedPlaces, textAnchor,
} from '@/lib/display'

interface Row {
  /** The device whose reading is shown; '' is this hornet. */
  device: string
  metric: string
  label: string
  x: number
  y: number
  size: number
  align: Align
  /** A caption the person typed (no source): its text; null for a reading. */
  text: string | null
  /** Font family id ('' is the standard one) and the size in pixels (null: by `size`), for screens the hive draws. */
  font: string
  px: number | null
  bold: boolean
  italic: boolean
}

const noStyle = { text: null, font: '', px: null, bold: false, italic: false }

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
        const valueTop = row.y + (row.label && row.text === null ? labelHeight : 0)
        const height = row.px ?? pixelHeight(row.size, screen.height)
        return (
          <g key={i} className={editable ? 'cursor-move' : undefined}
            onPointerDown={(e) => {
              if (!editable) return
              const p = point(e)
              drag.current = { i, dx: p.x - row.x, dy: p.y - row.y }
              svg.current?.setPointerCapture(e.pointerId)
            }}>
            {row.label && row.text === null && (
              <text x={row.x} y={row.y} dominantBaseline="hanging" textAnchor={textAnchor(row.align)} fontSize={7} fill="#fff" fontFamily="monospace">{row.label}</text>
            )}
            {/* digits take about 0.72 of the font size from the top */}
            <text x={row.x} y={valueTop} dominantBaseline="hanging" textAnchor={textAnchor(row.align)} fontSize={height / 0.72} fill="#fff" fontFamily="sans-serif" fontWeight={row.bold ? 700 : 400} fontStyle={row.italic ? 'italic' : 'normal'}>{texts[i]}</text>
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
  const [rotation, setRotation] = useState(0)
  const fonts = useQuery({ queryKey: ['display-fonts'], queryFn: () => call(api.GET('/api/display/fonts')) })
  const screen: Screen = current.data?.screen ?? fallbackScreen
  useEffect(() => {
    if (!current.data) return
    setRows(current.data.items.map((i) => ({
      device: i.device ?? '', metric: i.metric, label: i.label ?? '',
      x: i.x ?? 0, y: i.y ?? 0, size: i.size ?? 3, align: (i.align as Align | null) ?? 'left',
      text: i.text ?? null, font: i.font ?? '', px: i.px ?? null, bold: i.bold ?? false, italic: i.italic ?? false,
    })))
    setPlaced(current.data.items.some((i) => i.size != null))
    setRotation(current.data.rotation ?? 0)
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
  const rendered = !!current.data?.screen?.rendered
  const tabs = current.data?.screen?.tabs ?? 0
  const styled = rendered && placed  // fonts and sizes in pixels: only where the hive draws and the rows are placed
  const fontList: FontInfo[] = fonts.data ?? []

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
    else setRows((all) => all.filter((r) => r.text === null).slice(0, maxAuto))
  }

  function addRow() {
    setRows((all) => [...all, {
      device: '', metric: metricsOf(deviceId)[0] ?? '', label: '',
      x: 0, y: Math.min(screen.height - 1, all.length * 10), size: 3, align: 'left', ...noStyle,
    }])
  }

  function addText() {
    setRows((all) => [...all, {
      device: '', metric: '', label: '', x: 0, y: Math.min(screen.height - 1, all.length * 10), size: 3, align: 'left', ...noStyle, text: '',
    }])
  }

  const complete = rows.every((r) => (r.text === null ? r.metric !== '' : r.text.trim() !== ''))
  const texts = rows.map((r) => r.text ?? screenText(r.metric, valueOf(r.device || deviceId, r.metric)))
  const number = (v: string, min: number, max: number) => Math.min(max, Math.max(min, Math.round(Number(v) || 0)))

  async function save() {
    const items = rows.map((r) => ({
      device: r.text === null ? r.device || null : null, metric: r.text === null ? r.metric : '', label: r.text === null ? r.label.trim() || null : null,
      ...(r.text !== null ? { text: r.text.trim() } : {}),
      ...(placed ? { x: r.x, y: r.y, size: r.size, align: r.align === 'left' ? null : r.align } : {}),
      ...(styled ? { font: r.font || null, px: r.px, bold: r.bold, italic: r.italic } : {}),
    }))
    const { response } = await api.PUT('/api/devices/{deviceId}/display', {
      params: { path: { deviceId } }, body: { items, ...(rendered ? { rotation } : {}) },
    })
    if (!response.ok) {
      toast.error(t('errors.generic'))
      return
    }
    toast.success(t('display.saved'))
    await qc.invalidateQueries({ queryKey: key })
  }

  async function uploadBackground(file: File) {
    const { response, error } = await api.PUT('/api/devices/{deviceId}/display/background', {
      params: { path: { deviceId } }, body: file as unknown as string,
      bodySerializer: (body) => body as unknown as Blob, headers: { 'Content-Type': 'application/octet-stream' },
    })
    if (!response.ok) {
      const why = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.file?.[0]
      toast.error(`${t('display.backgroundWrong')}: ${why ?? t('errors.generic')}`)
      return
    }
    toast.success(t('display.backgroundSaved'))
    await qc.invalidateQueries({ queryKey: key })
  }

  async function removeBackground() {
    await api.DELETE('/api/devices/{deviceId}/display/background', { params: { path: { deviceId } } })
    toast.success(t('display.backgroundRemoved'))
    await qc.invalidateQueries({ queryKey: key })
  }

  async function uploadFont(file: File) {
    const { response, error } = await api.POST('/api/display/fonts', {
      body: file as unknown as string, bodySerializer: (body) => body as unknown as Blob, headers: { 'Content-Type': 'application/octet-stream' },
    })
    if (!response.ok) {
      toast.error((error as { errors?: Record<string, string[]> } | undefined)?.errors?.file?.[0] ?? t('errors.generic'))
      return
    }
    toast.success(t('display.fontSaved'))
    await qc.invalidateQueries({ queryKey: ['display-fonts'] })
  }

  async function removeFont(id: string) {
    await api.DELETE('/api/display/fonts/{familyId}', { params: { path: { familyId: id } } })
    toast.success(t('display.fontRemoved'))
    await qc.invalidateQueries({ queryKey: ['display-fonts'] })
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
      {rendered && (
        <label className="block max-w-xs space-y-1">
          <FieldLabel text={t('display.rotation')} help={t('display.rotationHelp')} />
          <Select value={rotation} disabled={!editable} onChange={(e) => setRotation(Number(e.target.value))}>
            {rotations.map((r) => <option key={r} value={r}>{r}°</option>)}
          </Select>
        </label>
      )}
      {rendered && (
        <div className="flex flex-wrap items-start gap-4">
          <div className="space-y-1">
            <p className="flex items-center gap-1 text-xs text-neutral-400">
              {t('display.rendered')}
              <HelpTip text={t('display.renderedHelp')} />
            </p>
            <img src={`/api/devices/${encodeURIComponent(deviceId)}/display/preview.png?v=${current.dataUpdatedAt}`}
              alt={t('display.rendered')} width={screen.width} height={screen.height}
              className="w-full max-w-80 rounded border border-neutral-700 bg-white" style={{ imageRendering: 'pixelated' }} />
          </div>
          <div className="min-w-48 space-y-2">
            <p className="flex items-center gap-1 text-xs text-neutral-400">
              {t('display.background')} {backgroundSize(screen)}
              <HelpTip text={t('display.backgroundHelp').replace('{size}', backgroundSize(screen))} />
            </p>
            {current.data?.screen?.background ? (
              <img src={`/api/devices/${encodeURIComponent(deviceId)}/display/background.png?v=${current.dataUpdatedAt}`}
                alt={t('display.background')} width={screen.width} height={screen.height}
                className="w-full max-w-40 rounded border border-neutral-700 bg-white" style={{ imageRendering: 'pixelated' }} />
            ) : (
              <p className="text-xs text-neutral-500">{t('display.backgroundNone')}</p>
            )}
            {editable && (
              <div className="flex flex-wrap gap-2">
                <label className="inline-flex cursor-pointer items-center gap-1 rounded-md border border-neutral-700 px-3 py-1.5 text-xs hover:bg-neutral-800">
                  <ImagePlus className="size-4" />{t('display.backgroundUpload')}
                  <input type="file" accept="image/png,image/jpeg,image/bmp,image/gif" className="sr-only"
                    onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) void uploadBackground(f) }} />
                </label>
                {current.data?.screen?.background && (
                  <Button variant="ghost" size="sm" onClick={() => void removeBackground()}><Trash2 />{t('display.backgroundRemove')}</Button>
                )}
              </div>
            )}
          </div>
        </div>
      )}
      {rendered && tabs > 0 && (
        <div className="space-y-1">
          <p className="flex items-center gap-1 text-xs text-neutral-400">
            {t('display.tabs')}
            <HelpTip text={t('display.tabsHelp')} />
          </p>
          <div className="flex flex-wrap gap-3">
            {[0, 1, 2].map((tab) => (
              <figure key={tab} className="space-y-1">
                <img src={`/api/devices/${encodeURIComponent(deviceId)}/display/preview.png?tab=${tab}&v=${current.dataUpdatedAt}`}
                  alt={t(`display.tab${tab}`)} width={screen.width} height={screen.height}
                  className="w-full max-w-64 rounded border border-neutral-700 bg-white" style={{ imageRendering: 'pixelated' }} />
                <figcaption className="text-xs text-neutral-500">{t(`display.tab${tab}`)}</figcaption>
              </figure>
            ))}
          </div>
        </div>
      )}
      {rows.length === 0 && <p className="text-xs text-neutral-400">{t('display.own')}</p>}
      {rows.map((row, i) => {
        const metrics = metricsOf(row.device || deviceId)
        return (
          <div key={i} className="space-y-2 rounded-lg border border-neutral-800 p-2">
            <div className="grid grid-cols-[1fr_1fr_auto] items-end gap-2 sm:grid-cols-[1fr_1fr_1fr_auto]">
              {row.text !== null ? (
                <label className="col-span-2 space-y-1 sm:col-span-3">
                  <FieldLabel text={t('display.text')} help={t('display.textHelp')} />
                  <Input value={row.text} maxLength={24} disabled={!editable} onChange={(e) => setRow(i, { text: e.target.value })} />
                </label>
              ) : (<>
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
              </>)}
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
            {styled && (
              <div className="grid grid-cols-2 items-end gap-2 sm:grid-cols-[2fr_1fr_auto_auto]">
                <label className="col-span-2 space-y-1 sm:col-span-1">
                  <FieldLabel text={t('display.font')} help={t('display.fontHelp')} />
                  <Select value={row.font} disabled={!editable} onChange={(e) => setRow(i, { font: e.target.value })}>
                    <option value="">{fontList.find((f) => f.id === 'standard')?.name ?? t('display.font')}</option>
                    {fontList.filter((f) => f.id !== 'standard').map((f) => <option key={f.id} value={f.id}>{f.name} · {fontBadge(f)}</option>)}
                    {row.font && !fontList.some((f) => f.id === row.font) && <option value={row.font}>{row.font}</option>}
                  </Select>
                </label>
                <label className="space-y-1">
                  <FieldLabel text={t('display.px')} help={t('display.pxHelp')} />
                  <Input type="number" min={minPx} max={maxPx} value={row.px ?? ''} disabled={!editable} placeholder={String(pixelHeight(row.size, screen.height))}
                    onChange={(e) => setRow(i, { px: e.target.value === '' ? null : clampPx(Number(e.target.value)) })} />
                </label>
                <label className="flex items-center gap-1 pb-2 text-xs text-neutral-300">
                  <input type="checkbox" checked={row.bold} disabled={!editable} onChange={(e) => setRow(i, { bold: e.target.checked })} />{t('display.bold')}
                </label>
                <label className="flex items-center gap-1 pb-2 text-xs text-neutral-300">
                  <input type="checkbox" checked={row.italic} disabled={!editable} onChange={(e) => setRow(i, { italic: e.target.checked })} />{t('display.italic')}
                </label>
              </div>
            )}
          </div>
        )
      })}
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" disabled={!editable || rows.length >= maxRows} onClick={addRow}><Plus />{t('display.add')}</Button>
        {styled && <Button variant="outline" disabled={!editable || rows.length >= maxRows} onClick={addText}><Type />{t('display.addText')}</Button>}
        <Button disabled={!editable || !complete} onClick={() => void save()}>{t('display.save')}</Button>
      </div>
      {!placed && <p className="text-xs text-neutral-500">{t('display.layout')}</p>}
      {rendered && editable && (
        <details className="text-xs text-neutral-400">
          <summary className="cursor-pointer">{t('display.ownFonts')}</summary>
          <div className="mt-2 space-y-2">
            <p className="text-neutral-500">{t('display.ownFontsHelp')}</p>
            <label className="inline-flex cursor-pointer items-center gap-1 rounded-md border border-neutral-700 px-3 py-1.5 hover:bg-neutral-800">
              <Upload className="size-4" />{t('display.fontUpload')}
              <input type="file" accept=".ttf,.otf,font/ttf,font/otf" className="sr-only"
                onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) void uploadFont(f) }} />
            </label>
            <ul className="space-y-1">
              {fontList.filter((f) => !f.builtin).map((f) => (
                <li key={f.id} className="flex items-center gap-2">
                  <span>{f.name} · {fontBadge(f)} · {f.styles.join(', ')}</span>
                  <Button variant="ghost" size="icon" className="size-7" title={t('display.fontRemove')} aria-label={t('display.fontRemove')}
                    onClick={() => void removeFont(f.id)}><Trash2 /></Button>
                </li>
              ))}
            </ul>
          </div>
        </details>
      )}
    </div>
  )
}
