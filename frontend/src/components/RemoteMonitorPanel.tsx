import { useQuery, useQueryClient } from '@tanstack/react-query'
import { MonitorUp } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { keys } from '@/api/queries'
import { FlashButton } from '@/components/FlashButton'
import { HelpTip } from '@/components/HelpTip'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'

/** Boards whose picture the hive draws (DisplayScreens.RenderedBoards) and the firmware build for each. */
const remoteBoards = [{ board: 'waveshare-s3-epaper-154', build: 'monitor-epd154' }]

const idPattern = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/

/**
 * A monitor far from the hive (contracts/monitor-http.md): it speaks only HTTPS with its screen token, so there is no
 * enrollment code; it is added here by hand and shows up online with its first read.
 */
export function RemoteMonitorPanel() {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const [deviceId, setDeviceId] = useState('monitor-')
  const [name, setName] = useState('')
  const [board, setBoard] = useState(remoteBoards[0].board)
  const [error, setError] = useState<string | null>(null)
  const firmware = useQuery({ queryKey: ['firmware'], queryFn: () => call(api.GET('/api/firmware')) })
  const build = remoteBoards.find((b) => b.board === board)?.build
  const hasBuild = (firmware.data ?? []).some((b) => b.build === build)
  const idValid = idPattern.test(deviceId) && deviceId.length <= 32

  async function add(e: FormEvent) {
    e.preventDefault()
    setError(null)
    const { data, error: err, response } = await api.POST('/api/devices/remote-monitors', { body: { deviceId, name: name.trim(), board } })
    if (!response.ok || !data) {
      setError((err as { errors?: Record<string, string[]> } | undefined)?.errors?.deviceId?.[0] ?? t('errors.generic'))
      return
    }
    toast.success(t('remoteMonitor.added'))
    await qc.invalidateQueries({ queryKey: keys.devices })
    void navigate(`/swarm/${data.deviceId}`)
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('remoteMonitor.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="flex items-start gap-1 text-xs text-neutral-500">
          <span>{t('remoteMonitor.hint')}</span>
          <HelpTip text={t('remoteMonitor.help')} />
        </p>
        <form className="space-y-2" onSubmit={(e) => void add(e)}>
          <label className="block space-y-1">
            <span className="text-xs text-neutral-400">{t('remoteMonitor.board')}</span>
            <Select value={board} onChange={(e) => setBoard(e.target.value)}>
              {remoteBoards.map((b) => <option key={b.board} value={b.board}>{t(`boards.${b.board}`, b.board)}</option>)}
            </Select>
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-neutral-400">{t('remoteMonitor.id')}</span>
            <Input value={deviceId} onChange={(e) => setDeviceId(e.target.value.toLowerCase())} aria-invalid={!idValid} />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-neutral-400">{t('remoteMonitor.name')}</span>
            <Input value={name} placeholder={t('remoteMonitor.namePlaceholder')} onChange={(e) => setName(e.target.value)} />
          </label>
          {error && <p className="text-xs text-red-400">{error}</p>}
          <Button type="submit" disabled={!idValid || name.trim() === ''}><MonitorUp />{t('remoteMonitor.add')}</Button>
        </form>
        <div className="space-y-1 border-t border-neutral-800 pt-3">
          <p className="text-xs text-neutral-400">{t('remoteMonitor.flash')}</p>
          {build && hasBuild
            ? <FlashButton build={build} />
            : <p className="text-xs text-neutral-500">{t('remoteMonitor.noBuild').replace('{build}', build ?? '')}</p>}
        </div>
      </CardContent>
    </Card>
  )
}
