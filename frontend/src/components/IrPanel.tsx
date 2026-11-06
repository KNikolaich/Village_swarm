import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Radio, Send, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { keys, useSendCommand } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'

type Code = { protocol: string; bits: number; data: string }

/** "NEC 32 0x02FD807F" (what the device last saw, kept by the hive as the state ir_last) as a code. */
function parseLast(text: unknown): Code | null {
  if (typeof text !== 'string') return null
  const [protocol, bits, data] = text.split(' ')
  const n = Number(bits)
  return protocol && data && Number.isInteger(n) ? { protocol, bits: n, data } : null
}

/** A device whose infrared receiver may be pointed at a remote: its name and the state it keeps (ir_last). */
export type IrReceiver = { deviceId: string; name: string; state: unknown }

/**
 * Remote control over infrared (a Tasmota device with an IR LED and receiver, docs/tasmota.md): point the real remote at
 * the device and press a button, the code appears here, give it a name; the named buttons are then sent from the hive.
 */
export function IrPanel({ deviceId, state, online, canEdit, receivers }: {
  deviceId: string
  state: unknown
  online: boolean
  canEdit: boolean
  /** A transmitter has no receiver of its own: the codes are learned from one of these. Absent: from this device. */
  receivers?: readonly IrReceiver[]
}) {
  const qc = useQueryClient()
  const [sourceId, setSourceId] = useState('')
  const source = receivers ? (receivers.find((r) => r.deviceId === sourceId) ?? receivers[0]) : undefined
  // The code the remote just sent comes with the device's state, which the page reads once a minute: look more often while learning.
  useEffect(() => {
    if (!receivers || !canEdit) return
    const timer = setInterval(() => void qc.invalidateQueries({ queryKey: keys.devices }), 3000)
    return () => clearInterval(timer)
  }, [qc, receivers, canEdit])
  const send = useSendCommand(deviceId)
  const [name, setName] = useState('')
  const key = ['ir-buttons', deviceId]
  const buttons = useQuery({ queryKey: key, queryFn: () => call(api.GET('/api/devices/{deviceId}/ir-buttons', { params: { path: { deviceId } } })) })

  const last = parseLast(((receivers ? source?.state : state) as Record<string, unknown> | null)?.ir_last)
  const stored = buttons.data?.buttons ?? {}
  const known = last ? Object.entries(stored).find(([, c]) => c.protocol.toUpperCase() === last.protocol.toUpperCase() && c.data.toLowerCase() === last.data.toLowerCase())?.[0] : undefined
  const unknownProtocol = last?.protocol.toUpperCase() === 'UNKNOWN'

  async function save(next: Record<string, Code>) {
    const { response } = await api.PUT('/api/devices/{deviceId}/ir-buttons', { params: { path: { deviceId } }, body: { buttons: next } })
    if (!response.ok) {
      toast.error(t('errors.generic'))
      return
    }
    await qc.invalidateQueries({ queryKey: key })
  }

  async function add() {
    if (!last || !name.trim()) return
    await save({ ...stored, [name.trim()]: last })
    setName('')
  }

  async function remove(button: string) {
    if (!window.confirm(t('ir.removeConfirm').replace('{name}', button))) return
    await save(Object.fromEntries(Object.entries(stored).filter(([n]) => n !== button)))
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('ir.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-4 text-sm">
        <p className="text-xs text-neutral-500">{t('ir.hint')}</p>

        {Object.keys(stored).length === 0 && <p className="text-neutral-500">{t('ir.none')}</p>}
        <ul className="space-y-2">
          {Object.entries(stored).map(([button, code]) => (
            <li key={button} className="flex items-center justify-between gap-2">
              <div className="min-w-0">
                <p className="truncate font-medium">{button}</p>
                <p className="truncate font-mono text-xs text-neutral-500">{code.protocol} {code.bits} {code.data}</p>
              </div>
              <div className="flex shrink-0 gap-1">
                <Button size="sm" variant="outline" disabled={!online} onClick={() => void send('ir', { button })}><Send />{t('ir.send')}</Button>
                {canEdit && <Button size="icon" variant="ghost" title={t('ir.remove')} onClick={() => void remove(button)}><Trash2 /></Button>}
              </div>
            </li>
          ))}
        </ul>

        {canEdit && (
          <div className="space-y-2 border-t border-neutral-800 pt-3">
            <p className="flex items-center gap-2 text-neutral-300"><Radio className="size-4 text-amber-400" />{t('ir.learn')}</p>
            {receivers && receivers.length === 0 && <p className="text-amber-300">{t('ir.noReceiver')}</p>}
            {receivers && receivers.length > 0 && (
              <label className="block space-y-1">
                <span className="text-xs text-neutral-400">{t('ir.receiver')}</span>
                <Select value={source?.deviceId} onChange={(e) => setSourceId(e.target.value)}>
                  {receivers.map((r) => <option key={r.deviceId} value={r.deviceId}>{r.name}</option>)}
                </Select>
              </label>
            )}
            {!last && !(receivers && receivers.length === 0) && <p className="text-neutral-500">{receivers ? t('ir.waitingReceiver') : t('ir.waiting')}</p>}
            {last && (
              <>
                <p className="font-mono text-xs text-amber-200">{last.protocol} {last.bits} {last.data}</p>
                {unknownProtocol && <p className="text-amber-300">{t('ir.unknownProtocol')}</p>}
                {known && <p className="text-emerald-400">{t('ir.alreadySaved').replace('{name}', known)}</p>}
                {!unknownProtocol && (
                  <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); void add() }}>
                    <Input placeholder={t('ir.namePlaceholder')} value={name} maxLength={32} onChange={(e) => setName(e.target.value)} />
                    <Button type="submit" disabled={!name.trim()}>{t('ir.save')}</Button>
                  </form>
                )}
              </>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
