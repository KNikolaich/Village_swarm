import { useQueryClient } from '@tanstack/react-query'
import { Pencil, Trash2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type LatestValueDto, type ZoneDto } from '@/api/client'
import { keys } from '@/api/queries'
import { DashboardChecks } from '@/components/DashboardChecks'
import { Dialog } from '@/components/notes/Dialog'
import { Button } from '@/components/ui/button'
import { Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { zoneKinds } from '@/lib/dashboard'

type Kind = (typeof zoneKinds)[number]

/**
 * One climate zone, edited in one place (admin): its name, kind and place in the list, its hornets with what each
 * shows on the zone (and a pencil to the hornet's card, which leads back here), adding a hornet, removing the zone.
 */
export function ZoneEditor({ zone, devices, latest, onClose }: {
  zone: ZoneDto
  /** The whole swarm: the zone's hornets and the ones without a zone that can be added. */
  devices: readonly DeviceDto[]
  latest: readonly LatestValueDto[]
  onClose: () => void
}) {
  const qc = useQueryClient()
  const [name, setName] = useState(zone.name)
  const [kind, setKind] = useState<Kind>(zone.kind as Kind)
  const [sort, setSort] = useState(String(zone.sort))
  const members = devices.filter((d) => d.zoneId === zone.id)
  const loose = devices.filter((d) => d.zoneId == null && d.type !== 'hive')
  const sortValue = Number(sort)
  const sortOk = sort.trim() !== '' && Number.isInteger(sortValue) && sortValue >= 0 && sortValue <= 9999
  const changed = name.trim() !== zone.name || kind !== zone.kind || sortValue !== zone.sort

  const refresh = () => Promise.all([qc.invalidateQueries({ queryKey: ['zones'] }), qc.invalidateQueries({ queryKey: keys.devices })])

  async function save(e: FormEvent) {
    e.preventDefault()
    const response = await api.PUT('/api/zones/{id}', { params: { path: { id: zone.id } }, body: { name: name.trim(), kind, sort: sortValue } })
    if (response.response.status === 409) toast.error(t('climate.exists'))
    else if (!response.response.ok) toast.error(t('climate.failed'))
    else toast.success(t('climate.saved'))
    await refresh()
  }

  async function assign(deviceId: string, zoneId: number | null) {
    try {
      await call(api.PUT('/api/devices/{deviceId}/zone', { params: { path: { deviceId } }, body: { zoneId } }))
      await refresh()
    } catch {
      toast.error(t('climate.failed'))
    }
  }

  async function remove() {
    if (!window.confirm(t('climate.deleteConfirm').replace('{name}', zone.name))) return
    await call(api.DELETE('/api/zones/{id}', { params: { path: { id: zone.id } } }))
    await refresh()
    onClose()
  }

  return (
    <Dialog title={t('climate.editZone').replace('{name}', zone.name)} onClose={onClose}>
      <div className="space-y-5 p-2">
        <form onSubmit={save} className="space-y-2">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder={t('climate.zoneName')} aria-label={t('climate.zoneName')} />
          <div className="flex flex-wrap items-center gap-2">
            <Select value={kind} onChange={(e) => setKind(e.target.value as Kind)} aria-label={t('climate.kind')}>
              {zoneKinds.map((k) => <option key={k} value={k}>{t(`climate.kinds.${k}`)}</option>)}
            </Select>
            <label className="flex items-center gap-2 text-sm text-neutral-400">
              {t('climate.sort')}
              <Input type="number" min={0} max={9999} value={sort} onChange={(e) => setSort(e.target.value)} className="h-9 w-20 text-sm" />
            </label>
            <Button type="submit" size="sm" className="ml-auto" disabled={!name.trim() || !sortOk || !changed}>{t('climate.save')}</Button>
          </div>
        </form>

        <div className="space-y-2">
          <p className="text-sm font-medium">{t('climate.hornets')}</p>
          {members.length === 0 && <p className="text-sm text-neutral-500">{t('climate.noHornets')}</p>}
          {members.map((d) => (
            <div key={d.deviceId} className="space-y-2 rounded-lg bg-neutral-800/50 p-3">
              <div className="flex items-center justify-between gap-2">
                <span className="min-w-0 truncate text-sm">{d.name}</span>
                <div className="flex shrink-0 items-center gap-1">
                  <Button variant="ghost" size="sm" className="text-xs text-neutral-400" onClick={() => void assign(d.deviceId, null)}>{t('climate.takeOut')}</Button>
                  <Link to={`/swarm/${d.deviceId}?zone=${zone.id}`} title={t('climate.editHornet')} aria-label={t('climate.editHornet')} className="p-1.5 text-neutral-400 hover:text-amber-400">
                    <Pencil className="size-4" />
                  </Link>
                </div>
              </div>
              <DashboardChecks device={d} latest={latest} editable compact />
            </div>
          ))}
          {loose.length > 0 && (
            <Select value="" onChange={(e) => e.target.value && void assign(e.target.value, zone.id)} className="w-full" aria-label={t('climate.addHornet')}>
              <option value="">{t('climate.addHornet')}</option>
              {loose.map((d) => <option key={d.deviceId} value={d.deviceId}>{d.name}</option>)}
            </Select>
          )}
        </div>

        <div className="flex justify-end border-t border-neutral-800 pt-3">
          <Button variant="outline" size="sm" className="border-red-900 text-red-400 hover:bg-red-950" onClick={() => void remove()}>
            <Trash2 />{t('climate.delete')}
          </Button>
        </div>
      </div>
    </Dialog>
  )
}
