import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, Copy } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'

const idPattern = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/

// Types that make sense for a switch or a sensor on Tasmota/OpenBeken.
const tasmotaTypes = ['heat', 'relay', 'meteo', 'leak']

type Setup = { deviceId: string; mqtt: { host: string; port: number; user: string | null; pass: string | null }; lines: string[] }

/**
 * A device that already runs Tasmota or OpenBeken (relay, socket, thermostat): the hive gives it a login of its own and
 * the lines to paste into the device's web console. Nothing is flashed from here: see docs/tasmota.md.
 */
export function TasmotaPanel({ presetId, presetName, presetType }: { presetId?: string; presetName?: string; presetType?: string }) {
  const [type, setType] = useState(presetType && tasmotaTypes.includes(presetType) ? presetType : 'heat')
  const [deviceId, setDeviceId] = useState(presetId ?? '')
  const [name, setName] = useState(presetName ?? '')
  const [maxOn, setMaxOn] = useState(360)
  const [setup, setSetup] = useState<Setup | null>(null)
  const [error, setError] = useState<string | null>(null)

  const online = useQuery({
    queryKey: ['device', setup?.deviceId],
    queryFn: async () => (await api.GET('/api/devices/{deviceId}', { params: { path: { deviceId: setup!.deviceId } } })).data ?? null,
    enabled: setup !== null,
    refetchInterval: 3000,
  })
  const isOnline = online.data?.status === 'online'

  async function create(e: FormEvent) {
    e.preventDefault()
    setError(null)
    const { data, error: err, response } = await api.POST('/api/provision/tasmota', {
      body: { deviceId, type, name, ...(type === 'heat' ? { maxOnMinutes: maxOn } : {}) },
    })
    if (!response.ok || !data) {
      setError((err as { errors?: Record<string, string[]> } | undefined)?.errors?.request?.[0] ?? t('errors.generic'))
      return
    }
    setSetup(data as Setup)
  }

  const idValid = idPattern.test(deviceId) && deviceId.length <= 32

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader><CardTitle>1. {t('tasmota.step1')}</CardTitle>{setup && <Badge tone="ok">{setup.deviceId}</Badge>}</CardHeader>
        <CardContent className="space-y-2 text-sm">
          <p className="text-neutral-400">{t('tasmota.intro')}</p>
          {!setup && (
            <form onSubmit={create} className="space-y-2">
              <label className="block space-y-1">
                <span className="text-xs text-neutral-400">{t('add.type')}</span>
                <Select value={type} onChange={(e) => setType(e.target.value)} className="h-11 w-full">
                  {tasmotaTypes.map((c) => <option key={c} value={c}>{t(`deviceTypes.${c}`)}</option>)}
                </Select>
              </label>
              <Input placeholder={t('add.idPlaceholder')} value={deviceId} onChange={(e) => setDeviceId(e.target.value.toLowerCase())} />
              {deviceId && !idValid && <p className="text-xs text-amber-300">{t('add.idHint')}</p>}
              <Input placeholder={t('add.namePlaceholder')} value={name} onChange={(e) => setName(e.target.value)} />
              {type === 'heat' && (
                <label className="block space-y-1">
                  <span className="text-xs text-neutral-400">{t('tasmota.maxOn')}</span>
                  <Input type="number" min={1} max={1080} value={maxOn} onChange={(e) => setMaxOn(Number(e.target.value))} />
                  <span className="block text-xs text-neutral-500">{t('tasmota.maxOnHint')}</span>
                </label>
              )}
              {error && <p className="text-sm text-red-400">{error}</p>}
              <Button type="submit" disabled={!idValid || !name.trim()}>{t('tasmota.create')}</Button>
            </form>
          )}
        </CardContent>
      </Card>

      {setup && (
        <Card>
          <CardHeader><CardTitle>2. {t('tasmota.step2')}</CardTitle>{isOnline && <Badge tone="ok"><CheckCircle2 className="size-3" />{t('status.online')}</Badge>}</CardHeader>
          <CardContent className="space-y-3 text-sm">
            <ol className="list-decimal space-y-1 pl-5 text-neutral-300">
              {t('tasmota.steps').split('|').map((s) => <li key={s}>{s}</li>)}
            </ol>
            <ul className="space-y-2">
              {setup.lines.map((line) => (
                <li key={line} className="flex items-start gap-2">
                  <code className="min-w-0 flex-1 break-all rounded bg-neutral-950 p-2 text-xs text-amber-200">{line}</code>
                  <Button variant="secondary" size="icon" onClick={() => void navigator.clipboard.writeText(line).then(() => toast.success(t('common.copied')))}><Copy /></Button>
                </li>
              ))}
            </ul>
            <p className="text-xs text-neutral-500">{t('tasmota.passwordOnce')}</p>
            {!isOnline && <p className="text-amber-300">{t('tasmota.waiting')}</p>}
            {isOnline && <p className="text-emerald-400">{t('add.done')}</p>}
          </CardContent>
        </Card>
      )}
    </div>
  )
}
