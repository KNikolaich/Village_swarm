import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { api, call } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Empty, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatTime } from '@/lib/format'

type Entry = { seq: number; at: string; topic: string; retained: boolean; payload: string }

const KEEP = 500

/**
 * Read-only window on the hive's MQTT traffic (admins only). The server keeps the last messages in memory; this page
 * polls for what is new. Nothing here can send a command: it needs no broker login and no password.
 */
export function MonitorPage() {
  const [entries, setEntries] = useState<Entry[]>([])
  const [filter, setFilter] = useState('')
  const [paused, setPaused] = useState(false)
  const [error, setError] = useState<string | null>(null)
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

  const shown = entries
    .filter((e) => !filter || e.topic.includes(filter) || e.payload.includes(filter))
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
              {e.retained && <span className="text-neutral-500"> [retained]</span>}
              <div className="text-neutral-300">{e.payload}</div>
            </div>
          ))}
        </CardContent>
      </Card>
    </div>
  )
}
