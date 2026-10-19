import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/primitives'
import { t } from '@/i18n'

const macPattern = /^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$/

/**
 * A Bluetooth bridge (type ble): the address of the thermometer it talks to, and the switch that puts the raw frames in its
 * log. Without an address the bridge lists the Bluetooth devices it hears, in the device's log (docs/ble-thermometer.md).
 */
export function BlePanel({ deviceId, editable }: { deviceId: string; editable: boolean }) {
  const qc = useQueryClient()
  const key = ['ble', deviceId]
  const current = useQuery({ queryKey: key, queryFn: () => call(api.GET('/api/devices/{deviceId}/ble', { params: { path: { deviceId } } })) })
  const [mac, setMac] = useState('')
  const [dump, setDump] = useState(false)
  useEffect(() => {
    if (current.data) {
      setMac(current.data.mac ?? '')
      setDump(current.data.dump)
    }
  }, [current.data])

  const valid = mac === '' || macPattern.test(mac)

  async function save() {
    const { response } = await api.PUT('/api/devices/{deviceId}/ble', { params: { path: { deviceId } }, body: { mac: mac || null, dump } })
    if (!response.ok) {
      toast.error(t('errors.generic'))
      return
    }
    toast.success(t('ble.saved'))
    await qc.invalidateQueries({ queryKey: key })
  }

  return (
    <div className="space-y-3 text-sm">
      <p className="text-xs text-neutral-500">{t('ble.hint')}</p>
      <label className="block space-y-1">
        <span className="text-xs text-neutral-400">{t('ble.mac')}</span>
        <Input className="font-mono" placeholder="AA:BB:CC:DD:EE:FF" value={mac} disabled={!editable} onChange={(e) => setMac(e.target.value.trim())} />
        {!valid && <span className="block text-xs text-amber-300">{t('ble.macBad')}</span>}
        {mac === '' && <span className="block text-xs text-neutral-500">{t('ble.macEmpty')}</span>}
      </label>
      <label className="flex cursor-pointer items-center gap-2">
        <input type="checkbox" className="size-4 accent-amber-400" checked={dump} disabled={!editable} onChange={(e) => setDump(e.target.checked)} />
        {t('ble.dump')}
      </label>
      <div className="flex items-center gap-3">
        <Button disabled={!editable || !valid} onClick={() => void save()}>{t('ble.save')}</Button>
      </div>
    </div>
  )
}
