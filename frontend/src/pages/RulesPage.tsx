import { useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { Link, useNavigate } from 'react-router'
import { toast } from 'sonner'
import { api, call, type RuleDto } from '@/api/client'
import { useDevices, useMe, useRules } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, Empty, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatWhen } from '@/lib/format'
import { describe, templates } from '@/lib/rules'

/** Screen 7 (spec 8.2): every rule in plain words, on/off at a click, the last time it acted. */
export function RulesPage() {
  const rules = useRules()
  const devices = useDevices()
  const me = useMe()
  const navigate = useNavigate()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const name = (id?: string | null) => (id ? devices.data?.find((d) => d.deviceId === id)?.name ?? id : t('rules.chooseDevice'))
  const list = rules.data ?? []

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-semibold">{t('rules.title')}</h1>
        {isAdmin && <Button onClick={() => navigate('/rules/new')}><Plus />{t('rules.new')}</Button>}
      </div>

      {rules.isLoading && <Skeleton className="h-32" />}
      {!rules.isLoading && list.length === 0 && (
        <Card>
          <CardContent className="space-y-3">
            <Empty>{t('rules.empty')}</Empty>
            {isAdmin && (
              <div className="flex flex-wrap justify-center gap-2">
                {templates(devices.data ?? []).map((tpl) => (
                  <Button key={tpl.key} variant="outline" onClick={() => navigate(`/rules/new?template=${tpl.key}`)}>{tpl.name}</Button>
                ))}
              </div>
            )}
          </CardContent>
        </Card>
      )}

      {list.map((rule) => <RuleRow key={rule.id} rule={rule} name={name} isAdmin={isAdmin} />)}
    </div>
  )
}

function RuleRow({ rule, name, isAdmin }: { rule: RuleDto; name: (id?: string | null) => string; isAdmin: boolean }) {
  const qc = useQueryClient()

  async function toggle() {
    try {
      await call(api.PUT('/api/rules/{id}', { params: { path: { id: rule.id } }, body: { name: rule.name, enabled: !rule.enabled, definition: rule.definition } }))
      await qc.invalidateQueries({ queryKey: ['rules'] })
    } catch {
      toast.error(t('errors.generic'))
    }
  }

  return (
    <Card className={rule.enabled ? undefined : 'opacity-60'}>
      <CardContent className="flex items-start justify-between gap-3 py-3">
        <Link to={`/rules/${rule.id}`} className="min-w-0 flex-1">
          <p className="font-medium hover:text-amber-400">{rule.name}</p>
          <p className="mt-0.5 text-sm text-neutral-400">{describe(rule.definition, name)}</p>
          <p className="mt-1 text-xs text-neutral-500">
            {rule.lastFiredAt ? t('rules.lastFired').replace('{when}', formatWhen(rule.lastFiredAt)) : t('rules.neverFired')}
            {rule.lastOutcome && rule.lastOutcome !== 'fired' && (
              <Badge tone="alarm" className="ml-2">{t(`rules.outcome.${rule.lastOutcome}`, rule.lastOutcome)}</Badge>
            )}
          </p>
        </Link>
        <label className="flex shrink-0 cursor-pointer items-center gap-2 text-xs text-neutral-400">
          <input type="checkbox" className="size-4 accent-amber-400" checked={rule.enabled} disabled={!isAdmin} onChange={() => void toggle()} />
          {rule.enabled ? t('rules.enabled') : t('rules.disabled')}
        </label>
      </CardContent>
    </Card>
  )
}
