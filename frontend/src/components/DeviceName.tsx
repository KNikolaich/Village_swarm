import { useQueryClient } from '@tanstack/react-query'
import { Check, Pencil, X } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api, call, type DeviceDto } from '@/api/client'
import { keys } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/primitives'
import { t } from '@/i18n'

/** The hornet's name as its page heading, with a pencil for admins (a typo made when adding it is fixed here). */
export function DeviceName({ device, editable }: { device: DeviceDto; editable: boolean }) {
  const qc = useQueryClient()
  const [draft, setDraft] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function save(e: FormEvent) {
    e.preventDefault()
    const name = (draft ?? '').trim()
    if (!name || name === device.name) {
      setDraft(null)
      return
    }
    setBusy(true)
    try {
      await call(api.PUT('/api/devices/{deviceId}/name', { params: { path: { deviceId: device.deviceId } }, body: { name } }))
      toast.success(t('swarm.renamed'))
      setDraft(null)
      await qc.invalidateQueries({ queryKey: keys.devices })
    } catch {
      toast.error(t('swarm.renameFailed'))
    } finally {
      setBusy(false)
    }
  }

  if (draft === null)
    return (
      <div className="flex min-w-0 items-center gap-1">
        <h1 className="truncate text-lg font-semibold">{device.name}</h1>
        {editable && (
          <Button variant="ghost" size="icon" className="size-7 shrink-0 text-neutral-500" title={t('swarm.rename')} aria-label={t('swarm.rename')} onClick={() => setDraft(device.name)}>
            <Pencil className="size-3.5" />
          </Button>
        )}
      </div>
    )

  return (
    <form onSubmit={save} className="flex items-center gap-1">
      <Input
        autoFocus
        value={draft}
        maxLength={128}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Escape') setDraft(null) }}
        className="h-9 min-w-0"
        aria-label={t('swarm.rename')}
      />
      <Button type="submit" variant="ghost" size="icon" className="size-8 shrink-0 text-emerald-400" disabled={busy || !draft.trim()} title={t('common.save')} aria-label={t('common.save')}><Check /></Button>
      <Button type="button" variant="ghost" size="icon" className="size-8 shrink-0" onClick={() => setDraft(null)} title={t('common.cancel')} aria-label={t('common.cancel')}><X /></Button>
    </form>
  )
}
