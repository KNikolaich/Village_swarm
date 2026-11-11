import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, ChevronUp, ImagePlus, Plus, Trash2, Type, Upload, X } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useDevices, useLatestTelemetry } from '@/api/queries'
import { HelpTip } from '@/components/HelpTip'
import { type SourceGroup, SourcePicker } from '@/components/SourcePicker'
import { Button } from '@/components/ui/button'
import { Input, Select } from '@/components/ui/primitives'
import { metricLabel, t } from '@/i18n'
import { type Align, backgroundSize, type BatteryState, bringBack, type ClockKind, outsideScreen, rowExtent, clockFormats, clockText, fontBadge, type FontInfo, maxAuto, maxPlaced, isOn, moved, nearestSize, pixelHeight, rotations, type Screen, screenText, sizeForPx, stackedPlaces, stateText, switchChannels } from '@/lib/display'
import { BatteryEditor } from '@/components/BatteryEditor'
import { FieldLabel, ScreenPreview } from '@/components/DisplayPreview'
import { clampInt, rowBox } from '@/lib/displayBox'
import { type Row, noStyle, statePrefix, measureTab, clockSource, fallbackScreen } from '@/lib/displayRows'

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
  const [invert, setInvert] = useState(false)
  /** The battery icon of a screen the hive draws without tabs (display.battery); x, y null: the top right corner. */
  const [battery, setBattery] = useState<BatteryState>({ enabled: true, x: null, y: null, scale: 1 })
  const fonts = useQuery({ queryKey: ['display-fonts'], queryFn: () => call(api.GET('/api/display/fonts')) })
  const screen: Screen = current.data?.screen ?? fallbackScreen
  useEffect(() => {
    if (!current.data) return
    setRows(current.data.items.map((i) => ({
      device: i.device ?? '', metric: i.metric, label: i.label ?? '',
      x: i.x ?? 0, y: i.y ?? 0, size: i.size ?? 3, align: (i.align as Align | null) ?? 'left',
      text: i.text ?? null, font: i.font ?? '', px: i.px ?? null, bold: i.bold ?? false, italic: i.italic ?? false,
      clock: (i.clock as ClockKind | null | undefined) ?? null, format: i.format ?? (i.clock ? clockFormats[i.clock as ClockKind][0] : ''),
      state: i.state ?? false, onText: i.onText ?? '', offText: i.offText ?? '', onImage: i.onImage ?? null, offImage: i.offImage ?? null,
    })))
    setPlaced(current.data.items.some((i) => i.size != null))
    setRotation(current.data.rotation ?? 0)
    setInvert(current.data.invert ?? false)
    setBattery({
      enabled: current.data.battery?.enabled ?? true, x: current.data.battery?.x ?? null, y: current.data.battery?.y ?? null,
      scale: current.data.battery?.scale ?? 1,
    })
  }, [current.data])

  // The readings each device has had lately: a screen can only show what the hive has seen.
  const metricsOf = useMemo(() => {
    const map = new Map<string, string[]>()
    for (const v of latest.data ?? []) map.set(v.deviceId, [...(map.get(v.deviceId) ?? []), v.metric])
    return (id: string) => (map.get(id) ?? []).slice().sort()
  }, [latest.data])
  const valueOf = (id: string, metric: string) => (latest.data ?? []).find((v) => v.deviceId === id && v.metric === metric)?.value
  const nameOf = (id: string) => (devices.data ?? []).find((d) => d.deviceId === id)?.name ?? id
  const stateOf = (id: string, channel: string) => isOn(((devices.data ?? []).find((d) => d.deviceId === id)?.state as Record<string, unknown> | null | undefined)?.[channel])
  const switchesOf = (id: string) => switchChannels((devices.data ?? []).find((d) => d.deviceId === id)?.state)
  const itemsOf = (id: string) => [
    ...metricsOf(id).map((m) => ({ value: m, label: metricLabel(m) })),
    ...switchesOf(id).map((c) => ({ value: statePrefix + c, label: `${t('display.state')}: ${c}` })),
  ]
  const sources = (devices.data ?? []).filter((d) => d.deviceId !== deviceId && itemsOf(d.deviceId).length > 0)
  const maxRows = placed ? maxPlaced : maxAuto
  const rendered = !!current.data?.screen?.rendered
  const tabs = current.data?.screen?.tabs ?? 0
  const styled = rendered && placed  // fonts and sizes in pixels: only where the hive draws and the rows are placed
  const fontList: FontInfo[] = fonts.data ?? []
  const sizesOf = (font: string) => (fontList.find((f) => f.id === (font || 'standard')) ?? fontList.find((f) => f.id === 'standard'))?.sizes ?? []
  /** The size in pixels a row is drawn at: its own, or the offered one nearest to its relative size. */
  const pxOf = (r: Row) => r.px ?? nearestSize(pixelHeight(r.size, screen.height), sizesOf(r.font))
  const thisName = (devices.data ?? []).find((d) => d.deviceId === deviceId)?.name ?? t('display.thisHornet')
  const groups: SourceGroup[] = [
    ...(itemsOf(deviceId).length > 0 ? [{ id: '', title: thisName, items: itemsOf(deviceId) }] : []),
    ...sources.map((d) => ({ id: d.deviceId, title: d.name, items: itemsOf(d.deviceId) })),
    { id: clockSource, title: t('display.clock'), items: [{ value: 'time', label: t('display.clockTime') }, { value: 'date', label: t('display.clockDate') }] },
  ]
  const sourceText = (r: Row) => r.clock
    ? `${t('display.clock')} · ${t(r.clock === 'time' ? 'display.clockTime' : 'display.clockDate')}`
    : r.metric ? `${r.device ? nameOf(r.device) : thisName} · ${r.state ? `${t('display.state')}: ${r.metric}` : metricLabel(r.metric)}` : t('display.pick')

  function setRow(i: number, patch: Partial<Row>) {
    setRows((all) => all.map((r, k) => {
      if (k !== i) return r
      const next = { ...r, ...patch }
      // another device: the first reading it has, when the old choice does not exist there
      if (patch.device !== undefined && next.clock === null && !next.state && !metricsOf(patch.device || deviceId).includes(next.metric)) next.metric = metricsOf(patch.device || deviceId)[0] ?? ''
      return next
    }))
  }

  function togglePlaced(on: boolean) {
    setPlaced(on)
    // switching on: the rows one under another, ready to be moved; switching off: the screen lays them out again
    if (on) setRows((all) => all.map((r, i) => ({ ...r, ...stackedPlaces(all.length, screen)[i], align: 'left' })))
    else setRows((all) => all.filter((r) => r.text === null).slice(0, maxAuto))
  }

  /** The rows in the order the person reads them: one place up (-1) or down (+1). */
  function move(i: number, by: -1 | 1) {
    setRows((all) => moved(all, i, by))
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

  const complete = rows.every((r) => (r.clock !== null || (r.text === null ? r.metric !== '' && (r.onImage === null || (!!r.onImage && !!r.offImage)) : r.text.trim() !== '')))
  const now = new Date()
  const texts = rows.map((r) => (r.clock ? clockText(r.format, now)
    : r.state ? stateText(stateOf(r.device || deviceId, r.metric), r.onText, r.offText)
    : r.text ?? screenText(r.metric, valueOf(r.device || deviceId, r.metric))))
  // how much room each row takes, and whether some of it is outside the screen (as far as the preview can tell)
  const boxes = rows.map((r, i) => {
    const size = rowBox({ ...r, px: styled ? pxOf(r) : r.px }, texts[i] ?? '', screen.height)
    return { ...size, out: outsideScreen(rowExtent(r, size.width, size.height), screen) }
  })

  async function save() {
    const items = rows.map((r) => {
      const reading = r.text === null && r.clock === null
      // where the hive draws, the size in pixels rules; the relative size only says the layout is placed
      const px = styled ? pxOf(r) : null
      return {
        device: reading ? r.device || null : null, metric: reading ? r.metric : '', label: reading ? r.label.trim() || null : null,
        ...(r.text !== null ? { text: r.text.trim() } : {}),
        ...(r.clock !== null ? { clock: r.clock, format: r.format } : {}),
        ...(reading && r.state ? {
          state: true, onText: r.onText.trim() || null, offText: r.offText.trim() || null,
          ...(styled && r.onImage && r.offImage ? { onImage: r.onImage, offImage: r.offImage } : {}),
        } : {}),
        ...(placed ? { x: r.x, y: r.y, size: px !== null ? sizeForPx(px, screen.height) : r.size, align: r.align === 'left' ? null : r.align } : {}),
        ...(styled ? { font: r.font || null, px, bold: r.bold, italic: r.italic } : {}),
      }
    })
    const { response } = await api.PUT('/api/devices/{deviceId}/display', {
      params: { path: { deviceId } },
      body: {
        items, ...(rendered ? { rotation, invert } : {}),
        // the battery icon: only a screen the hive draws without tabs has it; null for x and y is the top right corner
        ...(rendered && tabs === 0 ? { battery: { enabled: battery.enabled, x: battery.x, y: battery.y, scale: battery.scale } } : {}),
      },
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

  /** A picture for a state item: the hive keeps it and answers with its id (at most 100x100). */
  async function uploadImage(file: File): Promise<string | null> {
    const { response, data, error } = await api.POST('/api/devices/{deviceId}/display/images', {
      params: { path: { deviceId } }, body: file as unknown as string,
      bodySerializer: (body) => body as unknown as Blob, headers: { 'Content-Type': 'application/octet-stream' },
    })
    if (!response.ok || !data) {
      const why = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.file?.[0]
      toast.error(`${t('display.imageWrong')}: ${why ?? t('errors.generic')}`)
      return null
    }
    return data.id
  }

  async function uploadSleep(file: File) {
    const { response, error } = await api.PUT('/api/devices/{deviceId}/display/sleep', {
      params: { path: { deviceId } }, body: file as unknown as string,
      bodySerializer: (body) => body as unknown as Blob, headers: { 'Content-Type': 'application/octet-stream' },
    })
    if (!response.ok) {
      const why = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.file?.[0]
      toast.error(`${t('display.sleepWrong')}: ${why ?? t('errors.generic')}`)
      return
    }
    toast.success(t('display.sleepSaved'))
    await qc.invalidateQueries({ queryKey: key })
  }

  async function removeSleep() {
    await api.DELETE('/api/devices/{deviceId}/display/sleep', { params: { path: { deviceId } } })
    toast.success(t('display.sleepRemoved'))
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

  const picture = (tab: number | null) =>
    `/api/devices/${encodeURIComponent(deviceId)}/display/preview.png?${tab === null ? '' : `tab=${tab}&`}v=${current.dataUpdatedAt}`

  return (
    <div className="space-y-3 text-sm">
      <div className="flex flex-wrap items-end gap-x-6 gap-y-2">
        <label className="flex h-9 items-center gap-2 text-xs text-neutral-300">
          <input type="checkbox" checked={placed} disabled={!editable} onChange={(e) => togglePlaced(e.target.checked)} />
          {t('display.placed')}
          <HelpTip text={t('display.placedHelp').replace('{w}', String(screen.width)).replace('{h}', String(screen.height))} />
        </label>
        {rendered && (<>
          <label className="space-y-1">
            <FieldLabel text={t('display.rotation')} help={t('display.rotationHelp')} />
            <Select value={rotation} disabled={!editable} onChange={(e) => setRotation(Number(e.target.value))}>
              {rotations.map((r) => <option key={r} value={r}>{r}°</option>)}
            </Select>
          </label>
          <label className="flex h-9 items-center gap-2 text-xs text-neutral-300">
            <input type="checkbox" checked={invert} disabled={!editable} onChange={(e) => setInvert(e.target.checked)} />
            {t('display.invert')}
            <HelpTip text={t('display.invertHelp')} />
          </label>
        </>)}
      </div>
      {/* a screen that draws itself (the OLED): this sketch is all there is before saving, its rows are dragged here */}
      {!rendered && placed && rows.length > 0 && (
        <div className="space-y-1">
          <ScreenPreview screen={screen} rows={rows} texts={texts} editable={editable} onMove={(i, x, y) => setRow(i, { x, y })} />
          <p className="text-xs text-neutral-500">{t('display.previewHint').replace('{w}', String(screen.width)).replace('{h}', String(screen.height))}</p>
        </div>
      )}
      {/* the hive draws it: every tab as the panel will show it, and the background, in one row */}
      {rendered && (
        <div className="space-y-1">
          <p className="flex items-center gap-1 text-xs text-neutral-400">
            {tabs > 0 ? t('display.tabs') : t('display.rendered')}
            <HelpTip text={tabs > 0 ? `${t('display.tabsHelp')}\n${t('display.renderedHelp')}` : t('display.renderedHelp')} />
          </p>
          <div className="flex flex-wrap items-start gap-3">
            {(tabs > 0 ? Array.from({ length: tabs }, (_, tab) => tab) : [null]).map((tab) => (
              <figure key={tab ?? 'screen'} className="space-y-1">
                <div className="relative max-w-full" style={{ width: screen.width }}>
                  <img src={picture(tab)} alt={tab === null ? t('display.rendered') : t(`display.tab${tab}`)} width={screen.width} height={screen.height}
                    className="block h-auto w-full rounded border border-neutral-700 bg-white" style={{ imageRendering: 'pixelated' }} />
                  {/* the rows of Measure (or of the only screen) are dragged right on its picture */}
                  {editable && (tab === null || tab === measureTab) && ((placed && rows.length > 0) || (tabs === 0 && battery.enabled)) && (
                    <ScreenPreview overlay screen={screen} rows={placed ? rows.map((r) => ({ ...r, px: styled ? pxOf(r) : r.px })) : []} texts={placed ? texts : []} editable
                      onMove={(i, x, y) => setRow(i, { x, y })}
                      battery={tabs === 0 ? battery : null} onBattery={(x, y) => setBattery((b) => ({ ...b, x, y }))} />
                  )}
                </div>
                {tab !== null && <figcaption className="text-xs text-neutral-500">{t(`display.tab${tab}`)}</figcaption>}
              </figure>
            ))}
            <div className="min-w-40 space-y-2">
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
            <div className="min-w-40 space-y-2">
              <p className="flex items-center gap-1 text-xs text-neutral-400">
                {t('display.sleep')}
                <HelpTip text={t('display.sleepHelp').replace('{w}', String(screen.width)).replace('{h}', String(screen.height))} />
              </p>
              {current.data?.screen?.sleep ? (
                <img src={`/api/devices/${encodeURIComponent(deviceId)}/display/sleep.png?v=${current.dataUpdatedAt}`}
                  alt={t('display.sleep')} className="max-w-40 rounded border border-neutral-700 bg-white" style={{ imageRendering: 'pixelated' }} />
              ) : (
                <p className="text-xs text-neutral-500">{t('display.sleepNone')}</p>
              )}
              {editable && (
                <div className="flex flex-wrap gap-2">
                  <label className="inline-flex cursor-pointer items-center gap-1 rounded-md border border-neutral-700 px-3 py-1.5 text-xs hover:bg-neutral-800">
                    <ImagePlus className="size-4" />{t('display.sleepUpload')}
                    <input type="file" accept="image/png,image/jpeg,image/bmp,image/gif" className="sr-only"
                      onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) void uploadSleep(f) }} />
                  </label>
                  {current.data?.screen?.sleep && (
                    <Button variant="ghost" size="sm" onClick={() => void removeSleep()}><Trash2 />{t('display.sleepRemove')}</Button>
                  )}
                </div>
              )}
            </div>
          </div>
        </div>
      )}
      {rendered && tabs === 0 && <BatteryEditor battery={battery} screen={screen} editable={editable} onChange={setBattery} />}
      {/* a row whose text, as far as the preview can tell, is drawn outside the screen: shown, with a way back */}
      {placed && boxes.some((b) => b.out) && (
        <div className="space-y-1 rounded-lg border border-amber-700/60 bg-amber-950/20 p-2 text-xs text-amber-200">
          {rows.map((r, i) => !boxes[i].out ? null : (
            <div key={i} className="flex flex-wrap items-center gap-2">
              <span>{t('display.outside').replace('{n}', String(i + 1)).replace('{text}', texts[i] ?? '')}</span>
              <Button variant="outline" size="sm" disabled={!editable} onClick={() => setRow(i, bringBack(r, boxes[i].width, boxes[i].height, screen))}>{t('display.bringBack')}</Button>
            </div>
          ))}
        </div>
      )}
      {/* right under the pictures: what can be done with the rows, then the rows themselves */}
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" disabled={!editable || rows.length >= maxRows} onClick={addRow}><Plus />{t('display.add')}</Button>
        {styled && <Button variant="outline" disabled={!editable || rows.length >= maxRows} onClick={addText}><Type />{t('display.addText')}</Button>}
        <Button disabled={!editable || !complete} onClick={() => void save()}>{t('display.save')}</Button>
      </div>
      {!placed && <p className="text-xs text-neutral-500">{t('display.layout')}</p>}
      {rows.length === 0 && <p className="text-xs text-neutral-400">{t('display.own')}</p>}
      {rows.map((row, i) => {
        const sizes = sizesOf(row.font)
        const px = pxOf(row)
        const remove = (
          <div className="flex shrink-0 items-center">
            <Button variant="ghost" size="icon" className="size-9" disabled={!editable || i === 0} title={t('display.moveUp')} aria-label={t('display.moveUp')}
              onClick={() => move(i, -1)}><ChevronUp /></Button>
            <Button variant="ghost" size="icon" className="size-9" disabled={!editable || i === rows.length - 1} title={t('display.moveDown')} aria-label={t('display.moveDown')}
              onClick={() => move(i, 1)}><ChevronDown /></Button>
            <Button variant="ghost" size="icon" className="size-9" disabled={!editable} title={t('display.remove')} aria-label={t('display.remove')}
              onClick={() => setRows((all) => all.filter((_, k) => k !== i))}><X /></Button>
          </div>
        )
        return (
          <div key={i} className="space-y-2 rounded-lg border border-neutral-800 p-2">
            {/* first line: what (source, caption or format), how it is written (font, bold, italic) */}
            <div className={styled
              ? 'grid grid-cols-2 items-end gap-2 sm:grid-cols-[2fr_1.5fr_2fr_auto_auto]'
              : 'grid grid-cols-[1fr_1fr_auto] items-end gap-2'}>
              {row.text !== null ? (
                <label className="col-span-2 space-y-1">
                  <FieldLabel text={t('display.text')} help={t('display.textHelp')} />
                  <Input className="h-9 text-sm" value={row.text} maxLength={24} disabled={!editable} onChange={(e) => setRow(i, { text: e.target.value })} />
                </label>
              ) : (<>
                <div className="space-y-1">
                  <FieldLabel text={t('display.source')} help={t('display.sourceHelp')} />
                  <SourcePicker groups={groups} source={row.clock ? clockSource : row.device} value={row.clock ?? (row.state ? statePrefix + row.metric : row.metric)} text={sourceText(row)} disabled={!editable}
                    onPick={(source, value) => source === clockSource
                      ? setRow(i, { clock: value as ClockKind, format: clockFormats[value as ClockKind][0], device: '', metric: '', label: '' })
                      : value.startsWith(statePrefix)
                        ? setRow(i, { clock: null, format: '', device: source, metric: value.slice(statePrefix.length), state: true })
                        : setRow(i, { clock: null, format: '', device: source, metric: value, state: false, onImage: null, offImage: null })} />
                </div>
                {row.clock ? (
                  <label className="space-y-1">
                    <FieldLabel text={t('display.format')} help={t('display.formatHelp')} />
                    <Select className="w-full" value={row.format} disabled={!editable} onChange={(e) => setRow(i, { format: e.target.value })}>
                      {clockFormats[row.clock].map((f) => <option key={f} value={f}>{clockText(f, now)}</option>)}
                    </Select>
                  </label>
                ) : (
                  <label className="space-y-1">
                    <FieldLabel text={t('display.label')} help={t(placed ? 'display.labelPlacedHelp' : 'display.labelHelp')} />
                    <Input className="h-9 text-sm" value={row.label} maxLength={16} disabled={!editable} placeholder={!placed && row.device ? nameOf(row.device) : ''}
                      onChange={(e) => setRow(i, { label: e.target.value })} />
                  </label>
                )}
              </>)}
              {styled && (<>
                <label className="space-y-1">
                  <FieldLabel text={t('display.font')} help={t('display.fontHelp')} />
                  <Select className="w-full" value={row.font} disabled={!editable}
                    onChange={(e) => setRow(i, { font: e.target.value, px: nearestSize(px, sizesOf(e.target.value)) })}>
                    <option value="">{fontList.find((f) => f.id === 'standard')?.name ?? t('display.font')}</option>
                    {fontList.filter((f) => f.id !== 'standard').map((f) => <option key={f.id} value={f.id}>{f.name} · {fontBadge(f)}</option>)}
                    {row.font && !fontList.some((f) => f.id === row.font) && <option value={row.font}>{row.font}</option>}
                  </Select>
                </label>
                <div className="flex h-9 items-center gap-3 text-xs text-neutral-300">
                  <label className="flex items-center gap-1">
                    <input type="checkbox" checked={row.bold} disabled={!editable} onChange={(e) => setRow(i, { bold: e.target.checked })} />{t('display.bold')}
                  </label>
                  <label className="flex items-center gap-1">
                    <input type="checkbox" checked={row.italic} disabled={!editable} onChange={(e) => setRow(i, { italic: e.target.checked })} />{t('display.italic')}
                  </label>
                </div>
              </>)}
              {remove}
            </div>
            {/* a state (relay, switch): in words, or where the hive draws and the rows are placed, as two pictures */}
            {row.state && row.text === null && row.clock === null && (
              <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                {styled && (
                  <label className="space-y-1 sm:col-span-2">
                    <FieldLabel text={t('display.stateShow')} help={t('display.stateShowHelp')} />
                    <Select className="w-full sm:w-64" value={row.onImage !== null ? 'images' : 'words'} disabled={!editable}
                      onChange={(e) => setRow(i, e.target.value === 'images' ? { onImage: '', offImage: '' } : { onImage: null, offImage: null })}>
                      <option value="words">{t('display.stateWords')}</option>
                      <option value="images">{t('display.stateImages')}</option>
                    </Select>
                  </label>
                )}
                {row.onImage === null ? (<>
                  <label className="space-y-1">
                    <FieldLabel text={t('display.stateOn')} help={t('display.stateWordsHelp')} />
                    <Input className="h-9 text-sm" value={row.onText} maxLength={12} placeholder="вкл" disabled={!editable} onChange={(e) => setRow(i, { onText: e.target.value })} />
                  </label>
                  <label className="space-y-1">
                    <FieldLabel text={t('display.stateOff')} help={t('display.stateWordsHelp')} />
                    <Input className="h-9 text-sm" value={row.offText} maxLength={12} placeholder="выкл" disabled={!editable} onChange={(e) => setRow(i, { offText: e.target.value })} />
                  </label>
                </>) : (['on', 'off'] as const).map((which) => {
                  const id = which === 'on' ? row.onImage : row.offImage
                  return (
                    <div key={which} className="space-y-1">
                      <FieldLabel text={t(which === 'on' ? 'display.stateOn' : 'display.stateOff')} help={t('display.stateImageHelp')} />
                      <div className="flex items-center gap-2">
                        <div className="flex size-[104px] shrink-0 items-center justify-center rounded border border-neutral-700 bg-white">
                          {id ? <img src={`/api/devices/${encodeURIComponent(deviceId)}/display/images/${id}.png`} alt={t(which === 'on' ? 'display.stateOn' : 'display.stateOff')}
                            className="max-h-[100px] max-w-[100px]" style={{ imageRendering: 'pixelated' }} /> : <span className="text-xs text-neutral-500">{t('display.imageNone')}</span>}
                        </div>
                        {editable && (
                          <label className="inline-flex cursor-pointer items-center gap-1 rounded-md border border-neutral-700 px-3 py-1.5 text-xs hover:bg-neutral-800">
                            <ImagePlus className="size-4" />{t('display.imageUpload')}
                            <input type="file" accept="image/png,image/jpeg,image/bmp,image/gif" className="sr-only"
                              onChange={(e) => {
                                const f = e.target.files?.[0]
                                e.target.value = ''
                                if (f) void uploadImage(f).then((newId) => { if (newId) setRow(i, which === 'on' ? { onImage: newId } : { offImage: newId }) })
                              }} />
                          </label>
                        )}
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
            {/* second line: where and how big */}
            {placed && (
              <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
                <label className="space-y-1">
                  <FieldLabel text="X" help={t('display.xHelp').replace('{max}', String(screen.width - 1))} />
                  <Input className="h-9 text-sm" type="number" min={0} max={screen.width - 1} value={row.x} disabled={!editable}
                    onChange={(e) => setRow(i, { x: clampInt(e.target.value, 0, screen.width - 1) })} />
                </label>
                <label className="space-y-1">
                  <FieldLabel text="Y" help={t('display.yHelp').replace('{max}', String(screen.height - 1))} />
                  <Input className="h-9 text-sm" type="number" min={0} max={screen.height - 1} value={row.y} disabled={!editable}
                    onChange={(e) => setRow(i, { y: clampInt(e.target.value, 0, screen.height - 1) })} />
                </label>
                {styled ? (
                  <label className="space-y-1">
                    <FieldLabel text={t('display.px')} help={t('display.pxHelp')} />
                    <Select className="w-full" value={px} disabled={!editable} onChange={(e) => setRow(i, { px: Number(e.target.value) })}>
                      {!sizes.includes(px) && <option value={px}>{px}</option>}
                      {sizes.map((s) => <option key={s} value={s}>{s}</option>)}
                    </Select>
                  </label>
                ) : (
                  <label className="space-y-1">
                    <FieldLabel text={t('display.size')} help={t('display.sizeHelp').replace('{px}', String(pixelHeight(5, screen.height)))} />
                    <Input className="h-9 text-sm" type="number" min={1} max={10} value={row.size} disabled={!editable}
                      onChange={(e) => setRow(i, { size: clampInt(e.target.value, 1, 10) })} />
                  </label>
                )}
                <label className="space-y-1">
                  <FieldLabel text={t('display.align')} help={t('display.alignHelp')} />
                  <Select className="w-full" value={row.align} disabled={!editable} onChange={(e) => setRow(i, { align: e.target.value as Align })}>
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
