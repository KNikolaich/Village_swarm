import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { api, call } from '@/api/client'
import { useDevices } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Empty, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatTime } from '@/lib/format'

type Entry = { seq: number; at: string; topic: string; retained: boolean; payload: string }

const KEEP = 500

/** The device id a topic belongs to: vs/v1/dev/{id}/… → {id}. */
const deviceOf = (topic: string) => /^vs\/v1\/dev\/([^/]+)\//.exec(topic)?.[1] ?? null

/**
 * Read-only window on the hive's MQTT traffic (admins only). The server keeps the last messages in memory; this page
 * polls for what is new. Nothing here can send a command: it needs no broker login and no password.
 */
export function MonitorPage() {
  const [entries, setEntries] = useState<Entry[]>([])
  const [filter, setFilter] = useState('')
  const [paused, setPaused] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const devices = useDevices()
  const byId = new Map((devices.data ?? []).map((d) => [d.deviceId, d]))
  const cursor = useRef(0)

  useEffect(() => {
    if (paused) return
    let stop = false
    const poll = async () => {
      try {
        const page = await call(api.GET('/api/monitor', { params: { query: { since: cursor.current } } }))
        if (stop) return
        cursor.current = page.last
        if (page.entries.length) setEntries((old) => [...old, ...page.entries].slice(-KEEP))
        setError(null)
      } catch {
        if (!stop) setError(t('monitor.failed'))
      }
    }
    void poll()
    const id = window.setInterval(() => void poll(), 1500)
    return () => {
      stop = true
      window.clearInterval(id)
    }
  }, [paused])

  // A MAC or IP matches the hornet it belongs to (the hive knows it from enrollment and info), or any text in a message.
  const needle = filter.trim().toLowerCase()
  const fromMs = from ? new Date(from).getTime() : null
  const toMs = to ? new Date(to).getTime() : null
  const shown = entries
    .filter((e) => {
      if (fromMs !== null && new Date(e.at).getTime() < fromMs) return false
      if (toMs !== null && new Date(e.at).getTime() > toMs) return false
      if (!needle) return true
      const device = deviceOf(e.topic)
      const d = device ? byId.get(device) : undefined
      return e.topic.toLowerCase().includes(needle)
        || e.payload.toLowerCase().includes(needle)
        || (d?.mac ?? '').toLowerCase().includes(needle)
        || (d?.ip ?? '').toLowerCase().includes(needle)
    })
    .slice(-200)
    .reverse()

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-semibold">{t('monitor.title')}</h1>
        <Link to="/settings" className="text-sm text-amber-400">{t('nav.settings')}</Link>
      </div>
      <p className="text-xs text-neutral-500">{t('monitor.hint')}</p>
      <div className="flex flex-wrap gap-2">
        <Input placeholder={t('monitor.filter')} value={filter} onChange={(e) => setFilter(e.target.value)} className="max-w-xs" />
        <Input type="datetime-local" aria-label={t('monitor.from')} value={from} onChange={(e) => setFrom(e.target.value)} className="max-w-[12rem]" />
        <Input type="datetime-local" aria-label={t('monitor.to')} value={to} onChange={(e) => setTo(e.target.value)} className="max-w-[12rem]" />
        <Button variant="secondary" onClick={() => setPaused((p) => !p)}>{paused ? t('monitor.resume') : t('monitor.pause')}</Button>
        <Button variant="ghost" onClick={() => setEntries([])}>{t('monitor.clear')}</Button>
      </div>
      {error && <p className="text-sm text-red-400">{error}</p>}
      {shown.length === 0 && <Empty>{t('monitor.empty')}</Empty>}
      <Card>
        <CardContent className="max-h-[70vh] space-y-1 overflow-auto font-mono text-xs">
          {shown.map((e) => (
            <div key={e.seq} className="break-all border-b border-neutral-800 py-1">
              <span className="text-neutral-500">{formatTime(e.at)}</span>{' '}
              <span className="text-amber-300">{e.topic}</span>
              {deviceOf(e.topic) && byId.get(deviceOf(e.topic)!)?.mac && (
                <span className="text-neutral-500"> · {byId.get(deviceOf(e.topic)!)!.mac}{byId.get(deviceOf(e.topic)!)!.ip ? ` · ${byId.get(deviceOf(e.topic)!)!.ip}` : ''}</span>
              )}
              {e.retained && <span className="text-neutral-500"> [retained]</span>}
              <div className="text-neutral-300">{e.payload}</div>
            </div>
          ))}
        </CardContent>
      </Card>
    </div>
  )
}
