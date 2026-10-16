import { useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto } from '@/api/client'
import { useDevices } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { cn } from '@/lib/utils'

type Mode = 'stage' | 'switch'

/** Capabilities a hornet announced in its info message (contracts/schemas/info.schema.json "caps"). */
const hasCap = (d: DeviceDto, cap: string) => {
  const caps = (d.info as { caps?: unknown } | null)?.caps
  return Array.isArray(caps) && caps.includes(cap)
}

/** A deep sleeper is awake only for seconds: an order sent now would not reach it. */
const asleep = (d: DeviceDto) =>
  !!d.sleepS && d.sleepMode === 'deep' && !!d.lastSeenAt && Date.now() - new Date(d.lastSeenAt).getTime() > 90_000

/**
 * Hands the Wi-Fi network saved in Settings to chosen hornets, in one of two ways that are explained in full on the
 * screen: keep it as a spare (nothing changes now, safe) or switch to it now with a way back.
 */
export function WifiRolloutPanel({ ssid }: { ssid: string }) {
  const devices = useDevices()
  const [mode, setMode] = useState<Mode>('stage')
  const [chosen, setChosen] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [results, setResults] = useState<{ deviceId: string; name: string; result: string; mode: Mode }[] | null>(null)

  const hornets = (devices.data ?? []).filter((d) => d.status === 'online' && d.type !== 'hive')
  // Why a hornet cannot be picked in the current mode (empty: it can).
  const blocker = (d: DeviceDto): string => {
    if (asleep(d)) return t('settings.wifiPanel.asleep')
    if (mode === 'stage' && !hasCap(d, 'wifi.stage')) return t('settings.wifiPanel.oldFirmware')
    return ''
  }
  const pickable = chosen.filter((id) => { const d = hornets.find((x) => x.deviceId === id); return d && !blocker(d) })
  const names = pickable.map((id) => hornets.find((d) => d.deviceId === id)?.name ?? id)

  async function go() {
    const text = t(mode === 'stage' ? 'settings.wifiPanel.confirmStage' : 'settings.wifiPanel.confirmSwitch')
      .replace('{ssid}', ssid).replace('{names}', names.join(', '))
    if (pickable.length === 0 || !window.confirm(text)) return
    setBusy(true)
    try {
      const items = await call(api.POST('/api/settings/wifi/apply', { body: { deviceIds: pickable, mode } }))
      setResults(items.map((i) => ({ ...i, mode })))
      setChosen([])
    } catch {
      toast.error(t('errors.generic'))
    } finally {
      setBusy(false)
    }
  }

  const option = (value: Mode, recommended: boolean) => (
    <label
      className={cn('block cursor-pointer space-y-2 rounded-lg border p-3 text-sm', mode === value ? 'border-amber-400 bg-amber-400/5' : 'border-neutral-800')}
    >
      <span className="flex items-center gap-2">
        <input type="radio" name="wifi-mode" className="size-4 accent-amber-400" checked={mode === value} onChange={() => { setMode(value); setResults(null) }} />
        <span className="font-medium">{t(`settings.wifiPanel.${value}.title`)}</span>
        {recommended && <Badge tone="ok">{t('settings.wifiPanel.recommended')}</Badge>}
      </span>
      <span className="block text-neutral-300">{t(`settings.wifiPanel.${value}.what`)}</span>
      {mode === value && (
        <>
          <ol className="list-decimal space-y-0.5 pl-5 text-xs text-neutral-400">
            {t(`settings.wifiPanel.${value}.steps`).split('|').map((s) => <li key={s}>{s}</li>)}
          </ol>
          <span className="block text-xs text-neutral-500">{t(`settings.wifiPanel.${value}.when`)}</span>
        </>
      )}
    </label>
  )

  return (
    <div className="mt-4 space-y-3 border-t border-neutral-800 pt-3">
      <p className="text-sm font-medium">{t('settings.wifiPanel.title').replace('{ssid}', ssid)}</p>
      <p className="text-xs text-neutral-500">{t('settings.wifiPanel.intro')}</p>

      <div className="space-y-2">
        {option('stage', true)}
        {option('switch', false)}
      </div>

      <p className="text-xs text-neutral-400">{t('settings.wifiPanel.choose')}</p>
      {hornets.length === 0 && <p className="text-xs text-neutral-500">{t('settings.wifiApplyNobody')}</p>}
      <ul className="space-y-1">
        {hornets.map((d) => {
          const why = blocker(d)
          return (
            <li key={d.deviceId}>
              <label className={cn('flex items-center gap-2 text-sm', why ? 'cursor-not-allowed opacity-60' : 'cursor-pointer')}>
                <input
                  type="checkbox"
                  className="size-4 accent-amber-400"
                  disabled={!!why}
                  checked={chosen.includes(d.deviceId) && !why}
                  onChange={(e) => setChosen(e.target.checked ? [...chosen, d.deviceId] : chosen.filter((x) => x !== d.deviceId))}
                />
                <span>{d.name}</span>
                {why && <span className="text-xs text-amber-400">{why}</span>}
              </label>
            </li>
          )
        })}
      </ul>

      <div className="rounded-lg bg-neutral-800/50 p-3 text-xs text-neutral-300">
        {pickable.length === 0
          ? t('settings.wifiPanel.summaryNone')
          : t(mode === 'stage' ? 'settings.wifiPanel.summaryStage' : 'settings.wifiPanel.summarySwitch').replace('{ssid}', ssid).replace('{names}', names.join(', '))}
      </div>
      {mode === 'switch' && <p className="rounded-lg border border-amber-900/60 bg-amber-950/20 p-2 text-xs text-amber-300">{t('settings.wifiPanel.switchWarning')}</p>}

      <div className="flex flex-wrap items-center gap-3">
        <Button disabled={busy || pickable.length === 0} onClick={() => void go()}>
          {t(mode === 'stage' ? 'settings.wifiPanel.goStage' : 'settings.wifiPanel.goSwitch')}
        </Button>
        <Link to="/console" className="text-xs text-amber-400">{t('settings.wifiUsbRescue')}</Link>
      </div>

      {results && (
        <div className="space-y-2">
          <ul className="space-y-1 text-sm">
            {results.map((r) => (
              <li key={r.deviceId} className="flex items-center justify-between gap-3">
                <span className="min-w-0 truncate">{r.name}</span>
                <Badge tone={r.result === 'sent' ? 'ok' : r.result === 'failed' ? 'alarm' : 'neutral'}>{t(`settings.wifiResult.${r.result}`, r.result)}</Badge>
              </li>
            ))}
            {results.length === 0 && <li className="text-neutral-500">{t('settings.wifiApplyNone')}</li>}
          </ul>
          {results.some((r) => r.result === 'sent') && (
            <p className="text-xs text-neutral-400">
              {t(results[0].mode === 'stage' ? 'settings.wifiPanel.afterStage' : 'settings.wifiPanel.afterSwitch')}{' '}
              <Link to="/events" className="text-amber-400">{t('settings.wifiPanel.openEvents')}</Link>
            </p>
          )}
        </div>
      )}
    </div>
  )
}
