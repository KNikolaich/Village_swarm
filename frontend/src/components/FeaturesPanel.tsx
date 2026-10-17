import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, type DeviceDto } from '@/api/client'
import { keys } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { normalizeFeatures, type Feature } from '@/lib/features'

/**
 * What a hornet is used for: thermometer, BME280, relay, heater (as many as its firmware can do), and for a heater
 * the limits it keeps by itself. Saving sends a new config; the pins, readings and commands follow the choice.
 */
export function FeaturesPanel({ device, editable }: { device: DeviceDto; editable: boolean }) {
  const qc = useQueryClient()
  const supported = (device.supportedFeatures ?? []) as Feature[]
  const [draft, setDraft] = useState<Feature[] | null>(null)
  const [minC, setMinC] = useState('')
  const [maxC, setMaxC] = useState('')
  const [maxOnH, setMaxOnH] = useState('')
  const [busy, setBusy] = useState(false)
  const current = normalizeFeatures(device.features ?? [])
  const chosen = draft ?? current
  const heater = chosen.includes('heater')

  if (supported.length < 2) return null

  function toggle(f: Feature, on: boolean) {
    const next = on ? [...chosen, f] : chosen.filter((x) => x !== f && !(f === 'relay' && x === 'heater'))
    setDraft(normalizeFeatures(next))
  }

  const number = (text: string) => {
    const v = text.trim().replace(',', '.')
    return v === '' ? undefined : Number(v)
  }

  async function save() {
    const limits = { minC: number(minC), maxC: number(maxC), maxOnS: number(maxOnH) }
    if (Object.values(limits).some((v) => v !== undefined && !Number.isFinite(v))) {
      toast.error(t('features.notNumber'))
      return
    }
    setBusy(true)
    const { response, error } = await api.PUT('/api/devices/{deviceId}/features', {
      params: { path: { deviceId: device.deviceId } },
      body: {
        features: chosen,
        ...(heater ? { minC: limits.minC, maxC: limits.maxC, maxOnS: limits.maxOnS !== undefined ? Math.round(limits.maxOnS * 3600) : undefined } : {}),
      },
    })
    setBusy(false)
    if (!response.ok) {
      const reason = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.features?.[0]
      toast.error(reason ? `${t('features.rejected')}: ${reason}` : t('features.rejected'))
      return
    }
    setDraft(null)
    setMinC('')
    setMaxC('')
    setMaxOnH('')
    toast.success(t('swarm.pinsSaved'))
    await qc.invalidateQueries({ queryKey: keys.devices })
    await qc.invalidateQueries({ queryKey: ['devices', device.deviceId, 'pins'] })
  }

  const limits = device.heater
  const changed = draft !== null && draft.join() !== current.join()
  const limitsTyped = heater && (minC !== '' || maxC !== '' || maxOnH !== '')
  return (
    <div className="space-y-2 text-xs">
      <p className="text-neutral-500">{t('features.hint')}</p>
      <div className="flex flex-wrap gap-x-4 gap-y-2">
        {supported.map((f) => (
          <label key={f} className="flex items-center gap-2 text-sm text-neutral-300" title={t(`features.help.${f}`)}>
            <input type="checkbox" className="size-4 accent-amber-400" disabled={!editable} checked={chosen.includes(f)} onChange={(e) => toggle(f, e.target.checked)} />
            {t(`features.${f}`)}
          </label>
        ))}
      </div>
      {heater && (
        <div className="space-y-2">
          <p className="text-neutral-500">{t('features.heaterHint')}</p>
          <div className="grid grid-cols-3 gap-2">
            <label className="space-y-1">
              <span className="text-neutral-500">{t('features.minC')}</span>
              <Input inputMode="decimal" disabled={!editable} placeholder={String(limits?.minC ?? 5)} value={minC} onChange={(e) => setMinC(e.target.value)} className="h-8 text-xs" />
            </label>
            <label className="space-y-1">
              <span className="text-neutral-500">{t('features.maxC')}</span>
              <Input inputMode="decimal" disabled={!editable} placeholder={String(limits?.maxC ?? 25)} value={maxC} onChange={(e) => setMaxC(e.target.value)} className="h-8 text-xs" />
            </label>
            <label className="space-y-1">
              <span className="text-neutral-500">{t('features.maxOnH')}</span>
              <Input inputMode="decimal" disabled={!editable} placeholder={String((limits?.maxOnS ?? 21600) / 3600)} value={maxOnH} onChange={(e) => setMaxOnH(e.target.value)} className="h-8 text-xs" />
            </label>
          </div>
        </div>
      )}
      {editable && (
        <Button size="sm" className="w-full" onClick={() => void save()} disabled={busy || chosen.length === 0 || (!changed && !limitsTyped)}>{t('features.save')}</Button>
      )}
    </div>
  )
}
