import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { keys } from '@/api/queries'
import { Select } from '@/components/ui/primitives'
import { t } from '@/i18n'

/**
 * Which hive a hornet prefers to reach: the one it was enrolled with (this one, the default) or the other (the city or the dacha, as
 * named in Settings → fallback broker). It tries the preferred first and looks at it again from the other.
 */
export function PreferHivePanel({ deviceId, preferHive, editable }: { deviceId: string; preferHive?: string | null; editable: boolean }) {
  const qc = useQueryClient()
  const cluster = useQuery({ queryKey: ['cluster'], queryFn: () => call(api.GET('/api/cluster')), retry: false })
  if (!cluster.data?.enabled) return null
  const here = cluster.data.node === 'city' ? 'city' : 'dacha'
  const there = here === 'city' ? 'dacha' : 'city'

  async function choose(prefer: 'this' | 'other') {
    const { response } = await api.PUT('/api/devices/{deviceId}/prefer', { params: { path: { deviceId } }, body: { prefer } })
    if (!response.ok) {
      toast.error(prefer === 'other' ? t('prefer.needOther') : t('errors.generic'))
      return
    }
    toast.success(t('prefer.saved'))
    await qc.invalidateQueries({ queryKey: keys.devices })
  }

  return (
    <div className="space-y-2 text-sm">
      <p className="text-xs text-neutral-500">{t('prefer.hint')}</p>
      <label className="block space-y-1 sm:max-w-sm">
        <span className="text-xs text-neutral-400">{t('prefer.label')}</span>
        <Select value={preferHive === 'other' ? 'other' : 'this'} disabled={!editable} onChange={(e) => void choose(e.target.value as 'this' | 'other')}>
          <option value="this">{t(`prefer.${here}Here`)}</option>
          <option value="other">{t(`prefer.${there}`)}</option>
        </Select>
      </label>
    </div>
  )
}
