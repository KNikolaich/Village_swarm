import { useQueryClient } from '@tanstack/react-query'
import { RotateCw } from 'lucide-react'
import { toast } from 'sonner'
import { api, call, type DeviceDto } from '@/api/client'
import { keys } from '@/api/queries'
import { Card, CardContent, CardHeader, CardTitle, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { rotations } from '@/lib/video'

/**
 * «Поворот картинки» of a camera mounted on its side or upside down (config camera.rotate): the live video is turned in
 * the browser, photos by the hive when it stores them (the ones taken before stay as they were).
 */
export function RotationCard({ device }: { device: DeviceDto }) {
  const qc = useQueryClient()
  async function set(rotate: number) {
    try {
      await call(api.PUT('/api/devices/{deviceId}/rotate', { params: { path: { deviceId: device.deviceId } }, body: { rotate } }))
      toast.success(t('rotate.saved'))
      await qc.invalidateQueries({ queryKey: keys.devices })
      await qc.invalidateQueries({ queryKey: ['video', 'streams'] })
    } catch (e) {
      toast.error(`${t('rotate.refused')}: ${e instanceof Error ? e.message : String(e)}`)
    }
  }
  return (
    <Card>
      <CardHeader><CardTitle className="flex items-center gap-2"><RotateCw className="size-4" />{t('rotate.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-2 text-sm">
        <Select className="h-8 w-40 text-sm" value={String(device.rotate ?? 0)} aria-label={t('rotate.title')} onChange={(e) => void set(Number(e.target.value))}>
          {rotations.map((r) => <option key={r} value={r}>{t('rotate.degrees').replace('{d}', String(r))}</option>)}
        </Select>
        <p className="text-xs text-neutral-500">{t('rotate.hint')}</p>
      </CardContent>
    </Card>
  )
}
