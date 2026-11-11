import { Pencil, Search, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { api, call } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { type Nvr, type Probe } from '@/lib/ipcam'
import { LiveSnapshotButton, LoginLine, StreamLine, ChannelDiagnostics, ProbeLine } from '@/components/NvrLines'

export function NvrCard({ nvr, onEdit, onDelete }: { nvr: Nvr; onEdit: () => void; onDelete: () => void }) {
  const [probe, setProbe] = useState<Probe | null>(null)
  const [busy, setBusy] = useState(false)

  async function check() {
    setBusy(true)
    try {
      setProbe(await call(api.POST('/api/ipcams/{id}/probe', { params: { path: { id: nvr.id } } })))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{nvr.name}</CardTitle>
        <span className="text-xs text-neutral-500">{nvr.host}{nvr.port !== 80 ? `:${nvr.port}` : ''}</span>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        <ul className="space-y-2">
          {nvr.channels.map((c) => (
            <li key={c.channel} className="space-y-1">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="min-w-0 truncate">{t('ipcam.channel')} {c.channel} · {c.name}</span>
                <span className="flex flex-wrap items-center justify-end gap-2">
                  {c.snapshotVia && <span className="text-xs text-neutral-500">{t('ipcam.via').replace('{via}', c.snapshotVia)}</span>}
                  <Badge tone={c.status === 'online' ? 'ok' : c.status === 'offline' ? 'alarm' : 'neutral'}>{t(`status.${c.status}`, c.status)}</Badge>
                  <LiveSnapshotButton nvrId={nvr.id} channel={c.channel} />
                </span>
              </div>
              <ChannelDiagnostics channel={c} />
            </li>
          ))}
        </ul>
        <LoginLine login={nvr.login} />
        <StreamLine stream={nvr.stream} />
        {probe && <ProbeLine probe={probe} />}
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" size="sm" onClick={() => void check()} disabled={busy}><Search />{t('ipcam.probe')}</Button>
          <Button variant="secondary" size="sm" onClick={onEdit}><Pencil />{t('ipcam.edit')}</Button>
          <Button variant="ghost" size="sm" onClick={onDelete}><Trash2 />{t('ipcam.delete')}</Button>
        </div>
      </CardContent>
    </Card>
  )
}
