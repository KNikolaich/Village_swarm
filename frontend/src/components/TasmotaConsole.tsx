import { useState, type FormEvent } from 'react'
import { useSendCommand } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'

// Commands worth one click: what the device is, and what it has just heard.
const presets: { command: string; value: string }[] = [
  { command: 'Status', value: '0' },
  { command: 'RfReceive', value: '' },
  { command: 'Restart', value: '1' },
]

/**
 * The Tasmota console of one device, for administrators: any Tasmota command is sent as typed ("RfSend {...}", "PulseTime1 3700").
 * What the device answers, and what it reports by itself (a received RF or infrared code), shows up in the device log below.
 */
export function TasmotaConsole({ deviceId, online }: { deviceId: string; online: boolean }) {
  const send = useSendCommand(deviceId)
  const [command, setCommand] = useState('')
  const [value, setValue] = useState('')

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!command.trim()) return
    await send('tasmota', { command: command.trim(), value })
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('tasmotaConsole.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-xs text-neutral-500">{t('tasmotaConsole.hint')}</p>
        <form onSubmit={(e) => void submit(e)} className="flex flex-wrap gap-2">
          <Input className="w-40 font-mono" placeholder={t('tasmotaConsole.command')} value={command} onChange={(e) => setCommand(e.target.value)} />
          <Input className="min-w-0 flex-1 font-mono" placeholder={t('tasmotaConsole.value')} value={value} onChange={(e) => setValue(e.target.value)} />
          <Button type="submit" disabled={!online || !command.trim()}>{t('tasmotaConsole.send')}</Button>
        </form>
        <div className="flex flex-wrap gap-2">
          {presets.map((p) => (
            <Button key={p.command} size="sm" variant="outline" onClick={() => { setCommand(p.command); setValue(p.value) }}>
              {p.command}{p.value ? ` ${p.value}` : ''}
            </Button>
          ))}
        </div>
        <p className="text-xs text-neutral-500">{t('tasmotaConsole.secret')}</p>
      </CardContent>
    </Card>
  )
}
