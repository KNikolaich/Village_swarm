import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Usb } from 'lucide-react'
import { useState } from 'react'
import { FlashButton } from '@/components/FlashButton'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type Schemas } from '@/api/client'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'
import { formatAgo } from '@/lib/format'

/**
 * A meteo or relay hornet may move to the heat firmware of its board, which does all they do; a meteo hornet with a screen
 * to the monitor firmware (OtaService.CanBecome).
 */
const canBecome = (type: string, role: string) =>
  (role === 'heat' && (type === 'meteo' || type === 'relay')) || (role === 'monitor' && type === 'meteo')

/** Over-the-air update of one hornet: the builds the hive has for its role and board, the running one marked. */
export function FirmwarePanel({ device, builds, onDone }: { device: DeviceDto; builds: Schemas['FirmwareBuildDto'][]; onDone: () => void }) {
  const [busy, setBusy] = useState(false)
  const [usbOpen, setUsbOpen] = useState<string | null>(null)
  const qc = useQueryClient()
  const env = `${device.type}-${device.hw ?? '?'}`
  // A deep sleeper cannot take the command while it sleeps: the hive keeps the wish and sends it at the next wake-up.
  const sleeper = !!device.sleepS && device.sleepMode === 'deep'
  const waiting = useQuery({ queryKey: ['ota', 'pending'], queryFn: () => call(api.GET('/api/ota/pending')), enabled: sleeper })
  const pending = waiting.data?.find((p) => p.deviceId === device.deviceId)
  const suitable = builds.filter((b) => b.board === device.hw && (b.role === device.type || canBecome(device.type, b.role)))

  async function update(build: Schemas['FirmwareBuildDto']) {
    const risky = (device.hw ?? '').startsWith('esp-01')
    const text = build.role !== device.type
      ? t('swarm.becomeConfirm').replace('{name}', device.name).replace('{type}', t(`deviceTypes.${build.role}`, build.role))
      : t('swarm.updateConfirm').replace('{name}', device.name).replace('{from}', device.fw ?? '?').replace('{to}', build.version ?? '?')
    const notes = [risky ? t('swarm.updateRisk') : '', build.signed ? '' : t('swarm.updateUnsigned')].filter(Boolean)
    if (!window.confirm(notes.length ? `${text}\n\n${notes.join('\n\n')}` : text)) return
    setBusy(true)
    const { response, error } = await api.POST('/api/devices/{deviceId}/ota', { params: { path: { deviceId: device.deviceId } }, body: { build: build.build } })
    setBusy(false)
    if (response.ok) {
      toast.success(t('swarm.updateStarted'))
      onDone()
      return
    }
    const reason = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.ota?.[0]
    toast.error(reason ? `${t('swarm.updateRefused')}: ${reason}` : t('swarm.updateRefused'))
  }

  async function queue(build: Schemas['FirmwareBuildDto']) {
    const risky = (device.hw ?? '').startsWith('esp-01')
    const text = t('swarm.waitConfirm').replace('{name}', device.name).replace('{to}', build.version ?? '?')
    const notes = [risky ? t('swarm.updateRisk') : '', build.signed ? '' : t('swarm.updateUnsigned')].filter(Boolean)
    if (!window.confirm(notes.length ? `${text}\n\n${notes.join('\n\n')}` : text)) return
    setBusy(true)
    const { response, error } = await api.PUT('/api/devices/{deviceId}/ota/pending', { params: { path: { deviceId: device.deviceId } }, body: { build: build.build } })
    setBusy(false)
    if (response.ok) {
      toast.success(t('swarm.waitQueued'))
      await qc.invalidateQueries({ queryKey: ['ota', 'pending'] })
      return
    }
    const reason = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.ota?.[0]
    toast.error(reason ? `${t('swarm.updateRefused')}: ${reason}` : t('swarm.updateRefused'))
  }

  async function cancelWaiting() {
    setBusy(true)
    await api.DELETE('/api/devices/{deviceId}/ota/pending', { params: { path: { deviceId: device.deviceId } } })
    setBusy(false)
    await qc.invalidateQueries({ queryKey: ['ota', 'pending'] })
  }

  return (
    <div className="space-y-2 text-xs">
      <p className="text-neutral-400">{t('swarm.fwRunning').replace('{version}', device.fw ?? '?')}</p>
      {!suitable.some((b) => b.role === device.type) && <p className="text-neutral-500">{t('swarm.fwNone').replace('{env}', env)}</p>}
      {suitable.map((b) => {
        const current = b.version === device.fw
        return (
          <div key={b.build} className="flex items-center justify-between gap-2 border-t border-neutral-800 pt-2">
            <div>
              <p className="font-medium">{b.build} · {b.version ?? '?'}</p>
              {b.role !== device.type && <p className="text-amber-400">{t('swarm.fwBecome').replace('{type}', t(`deviceTypes.${b.role}`, b.role))}</p>}
              <p className="text-neutral-500">{t('swarm.fwBuilt').replace('{when}', formatAgo(b.built))}</p>
            </div>
            <div className="flex shrink-0 items-center gap-2">
              {current ? (
                <span className="text-neutral-500">{t('swarm.fwCurrent')}</span>
              ) : sleeper && pending?.build === b.build ? (
                <>
                  <span className="text-amber-400">{t('swarm.waitPending').replace('{n}', String(pending.attempts))}</span>
                  <Button size="sm" variant="outline" disabled={busy} onClick={() => void cancelWaiting()}>{t('common.cancel')}</Button>
                </>
              ) : sleeper ? (
                <Button size="sm" disabled={busy} onClick={() => void queue(b)}>{t('swarm.fwInstallOnWake')}</Button>
              ) : (
                <Button size="sm" disabled={busy || device.status !== 'online'} onClick={() => void update(b)}>{t('swarm.fwInstall')}</Button>
              )}
              <Button size="sm" variant="outline" onClick={() => setUsbOpen(usbOpen === b.build ? null : b.build)}><Usb />{t('swarm.fwUsb')}</Button>
            </div>
          </div>
        )
      })}
      {usbOpen && (
        <div className="space-y-2 rounded-lg border border-neutral-800 p-3">
          <p className="text-neutral-300">{t('swarm.fwUsbHint')}</p>
          <FlashButton build={usbOpen} />
        </div>
      )}
      {suitable.some((b) => b.role === device.type) && suitable.every((b) => b.role !== device.type || b.version === device.fw) && <p className="text-neutral-500">{t('swarm.fwNoNewer')}</p>}
      {device.status !== 'online' && <p className="text-amber-400">{t('swarm.fwOffline')}</p>}
    </div>
  )
}
