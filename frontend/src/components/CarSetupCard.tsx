import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Car } from 'lucide-react'
import { Link } from 'react-router'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { api, call, type Schemas } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { driveLink } from '@/lib/video'
import { driveRepeatMs, motorInputs, motorTestDrive, stopDrive, type MotorInput } from '@/lib/car'

type CarSetup = Schemas['CarSetupDto']

const defaultPins = [14, 15, 13, 12]
const pinLabels = ['IN1', 'IN2', 'IN3', 'IN4']

/**
 * One input of the L298N at a time, for 1 s at 60 %: shows which pin turns which wheels which way, so the pins and the
 * boxes above can be set. It is an ordinary drive (left or right wheels only), so the deadman and the limits stay.
 */
function MotorTest({ deviceId }: { deviceId: string }) {
  const [busy, setBusy] = useState(false)
  async function pulse(input: MotorInput) {
    setBusy(true)
    const drive = motorTestDrive(input)
    try {
      // The car stops 500 ms after the last drive: repeated for the second, then a stop.
      for (let i = 0; i < 5; i++) {
        await call(api.POST('/api/cars/{deviceId}/drive', { params: { path: { deviceId } }, body: drive }))
        await new Promise((r) => setTimeout(r, driveRepeatMs))
      }
    } catch (e) {
      toast.error(`${t('car.failed')}: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      await call(api.POST('/api/cars/{deviceId}/drive', { params: { path: { deviceId } }, body: stopDrive })).catch(() => {})
      setBusy(false)
    }
  }
  return (
    <div className="space-y-2 border-t border-neutral-800 pt-3">
      <p className="text-xs text-neutral-500">{t('car.test.hint')}</p>
      <div className="flex flex-wrap gap-2">
        {motorInputs.map((input) => (
          <Button key={input} size="sm" variant="outline" disabled={busy} onClick={() => void pulse(input)}>{t(`car.test.${input}`)}</Button>
        ))}
      </div>
    </div>
  )
}

/**
 * A car's wiring and limits (contracts/mqtt-topics.md "Car"): which pins drive the L298N, which way each motor turns, how
 * fast and how long it may go. The administrator changes them; everyone who may control sees them. Driving itself is on
 * the overview's «Видео» over the car's picture.
 */
export function CarSetupCard({ deviceId, editable }: { deviceId: string; editable: boolean }) {
  const qc = useQueryClient()
  const setup = useQuery({ queryKey: ['cars', deviceId, 'setup'], queryFn: () => call(api.GET('/api/cars/{deviceId}/setup', { params: { path: { deviceId } } })) })
  const [draft, setDraft] = useState<CarSetup | null>(null)
  useEffect(() => { if (setup.data) setDraft(setup.data) }, [setup.data])

  if (!draft) return null
  const pins = draft.motorPins ?? defaultPins
  const flag = (key: 'motorPwm' | 'invertLeft' | 'invertRight' | 'swapSides') => (
    <label className="flex items-center gap-2">
      <input type="checkbox" checked={draft[key] ?? false} disabled={!editable} onChange={(e) => setDraft({ ...draft, [key]: e.target.checked })} />
      {t(`car.setup.${key}`)}
    </label>
  )

  async function save() {
    if (!draft) return
    try {
      await call(api.PUT('/api/cars/{deviceId}/setup', { params: { path: { deviceId } }, body: draft }))
      toast.success(t('car.setup.saved'))
      await qc.invalidateQueries({ queryKey: ['cars', deviceId, 'setup'] })
    } catch (e) {
      toast.error(`${t('car.setup.refused')}: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle className="flex items-center gap-2"><Car className="size-4" />{t('car.setup.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        <Link to={driveLink(deviceId)} className="inline-flex items-center gap-2 rounded-md bg-amber-500 px-3 py-1.5 text-sm font-medium text-neutral-950 hover:bg-amber-400">
          <Car className="size-4" />{t('car.drive')}
        </Link>
        <p className="text-xs text-neutral-500">{t('car.setup.hint')}</p>
        <div className="grid grid-cols-5 gap-2">
          {pinLabels.map((label, i) => (
            <label key={label} className="space-y-1 text-xs">
              <span className="text-neutral-500">{label}{i < 2 ? ` (${t('car.setup.left')})` : ` (${t('car.setup.right')})`}</span>
              <Input className="h-8" inputMode="numeric" disabled={!editable} value={String(pins[i])}
                onChange={(e) => {
                  const next = [...pins]
                  next[i] = Number(e.target.value.replace(/\D/g, '') || 0)
                  setDraft({ ...draft, motorPins: next })
                }} />
            </label>
          ))}
          <label className="space-y-1 text-xs">
            <span className="text-neutral-500">{t('car.setup.lightPin')}</span>
            <Input className="h-8" inputMode="numeric" disabled={!editable} value={String(draft.lightPin ?? 4)}
              onChange={(e) => setDraft({ ...draft, lightPin: Number(e.target.value.replace(/\D/g, '') || 0) })} />
          </label>
        </div>
        <div className="grid gap-1 text-xs sm:grid-cols-2">
          {flag('motorPwm')}{flag('swapSides')}{flag('invertLeft')}{flag('invertRight')}
        </div>
        <div className="grid grid-cols-2 gap-2 text-xs">
          <label className="space-y-1">
            <span className="text-neutral-500">{t('car.setup.maxSpeed')}</span>
            <Input className="h-8" inputMode="numeric" disabled={!editable} value={String(draft.maxSpeed ?? 100)}
              onChange={(e) => setDraft({ ...draft, maxSpeed: Number(e.target.value.replace(/\D/g, '') || 0) })} />
          </label>
          <label className="space-y-1">
            <span className="text-neutral-500">{t('car.setup.minSpeed')}</span>
            <Input className="h-8" inputMode="numeric" disabled={!editable} value={String(draft.minSpeed ?? 40)}
              onChange={(e) => setDraft({ ...draft, minSpeed: Number(e.target.value.replace(/\D/g, '') || 0) })} />
          </label>
          <label className="space-y-1">
            <span className="text-neutral-500">{t('car.setup.maxRunS')}</span>
            <Input className="h-8" inputMode="numeric" disabled={!editable} value={String(draft.maxRunS ?? 60)}
              onChange={(e) => setDraft({ ...draft, maxRunS: Number(e.target.value.replace(/\D/g, '') || 0) })} />
          </label>
        </div>
        {editable && <Button size="sm" onClick={() => void save()}>{t('car.setup.save')}</Button>}
        {editable && <MotorTest deviceId={deviceId} />}
      </CardContent>
    </Card>
  )
}
