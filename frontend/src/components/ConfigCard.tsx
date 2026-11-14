import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, errorText } from '@/api/client'
import { useMe } from '@/api/queries'
import { RepositoryCheck } from '@/components/RepositoryCheck'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select, Skeleton } from '@/components/ui/primitives'
import { HelpTip } from '@/components/HelpTip'
import { t } from '@/i18n'

const queryKey = ['settings', 'config'] as const

/** "auth.newdevicecode" → "auth_newdevicecode": the dots would nest the i18n key. */
const i18nKey = (key: string) => key.replace(/\./g, '_')

type Item = {
  key: string
  section: string
  kind: string
  apply: string
  value: string
  stored?: string | null
  source: string
  default: string
  restartPending: boolean
  choices?: string[] | null
}

/**
 * Plain parameters that used to live only in .env, grouped by topic (admins only). A value set here wins over .env;
 * Reset returns to .env or the default. Most are read at start: the banner then points at the restart button below.
 */
export function ConfigCard({ only, except }: { only?: string[]; except?: string[] } = {}) {
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const config = useQuery({ queryKey, queryFn: () => call(api.GET('/api/settings/config')), enabled: isAdmin })
  if (!isAdmin) return null
  const items: Item[] = config.data?.items ?? []
  const pending = items.filter((i) => i.restartPending)
  return (
    <div className="space-y-4">
      {!only && <Card>
        <CardHeader><CardTitle className="flex items-center gap-1">{t('config.title')}<HelpTip text={t('config.hint')} /></CardTitle></CardHeader>
        <CardContent className="space-y-3 text-sm">
          {config.isLoading && <Skeleton className="h-24" />}
          {pending.length > 0 && (
            <div className="rounded-md border border-amber-500/40 bg-amber-500/10 p-2 text-xs text-amber-200">
              <p className="font-medium">{t('config.restartTitle')}</p>
              <p>{t('config.restartBody').replace('{names}', pending.map((i) => t(`config.params.${i18nKey(i.key)}.name`)).join(', '))}</p>
              <Link to="/settings?tab=maintenance#restart" className="mt-1 inline-block text-amber-400 underline">{t('config.restartGo')}</Link>
            </div>
          )}
        </CardContent>
      </Card>}
      {(config.data?.sections ?? []).filter((s) => (!only || only.includes(s)) && !except?.includes(s)).map((section) => {
        const rows = items.filter((i) => i.section === section)
        if (rows.length === 0) return null
        return (
          <Card key={section} id={`config-${section}`}>
            <CardHeader><CardTitle className="flex items-center gap-1">{t(`config.sections.${section}.title`)}<HelpTip text={sectionHelp(section, rows)} /></CardTitle></CardHeader>
            <CardContent className="space-y-4 text-sm">
              {rows.map((item) => <ConfigRow key={item.key} item={item} />)}
            </CardContent>
          </Card>
        )
      })}
    </div>
  )
}

/** One tooltip per group: the group's own hint, then every parameter's name and hint (with when it applies). */
function sectionHelp(section: string, rows: Item[]) {
  const params = rows.map((i) => `${t(`config.params.${i18nKey(i.key)}.name`)}: ${t(`config.params.${i18nKey(i.key)}.hint`)} ${t(i.apply === 'auto' ? 'config.autoHint' : 'config.restartHint')}`)
  return [t(`config.sections.${section}.hint`), ...params].join('\n\n')
}

function ConfigRow({ item }: { item: Item }) {
  const qc = useQueryClient()
  const shown = item.stored ?? item.value
  const [draft, setDraft] = useState(shown)
  const [busy, setBusy] = useState(false)
  useEffect(() => setDraft(shown), [shown])
  const id = `config-${item.key}`
  const name = t(`config.params.${i18nKey(item.key)}.name`)
  const changed = draft !== shown

  async function save(next: string) {
    setBusy(true)
    try {
      await call(api.PUT('/api/settings/config/{key}', { params: { path: { key: item.key } }, body: { value: next } }))
      await qc.invalidateQueries({ queryKey })
      toast.success(t(next === '' ? 'config.reseted' : item.apply === 'auto' ? 'config.saved' : 'config.savedRestart'))
    } catch (e) {
      toast.error(errorText(e, t('config.invalid')))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-1">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <label htmlFor={id} className="font-medium">{name}</label>
        <span className="flex flex-wrap items-center gap-1">
          {item.restartPending && <Badge tone="warn">{t('config.restartBadge')}</Badge>}
          {item.apply === 'auto' && <Badge tone="info">{t('config.autoBadge')}</Badge>}
          <Badge>{t(`config.source.${item.source}`)}</Badge>
        </span>
      </div>
      <div className="flex flex-wrap gap-2">
        <div className="min-w-40 flex-1">
          <Field id={id} item={item} value={draft} onChange={setDraft} label={name} />
        </div>
        <Button disabled={busy || !changed || (draft === '' && item.stored == null)} onClick={() => void save(draft.trim())}>{t('config.save')}</Button>
        {item.stored != null && (
          <Button variant="outline" disabled={busy} onClick={() => void save('')}>{t('config.reset')}</Button>
        )}
      </div>
      {item.key === 'backup.repository' && <RepositoryCheck address={draft} />}
      {item.default !== '' && <p className="text-xs text-neutral-600">{t('config.default').replace('{value}', defaultLabel(item))}</p>}
    </div>
  )
}

function defaultLabel(item: Item) {
  if (item.kind === 'bool') return t(item.default === 'true' ? 'config.yes' : 'config.no')
  if (item.kind === 'choice') return t(`config.choices.${i18nKey(item.key)}.${item.default}`, item.default)
  return item.default
}

function Field({ id, item, value, onChange, label }: { id: string; item: Item; value: string; onChange: (v: string) => void; label: string }) {
  if (item.kind === 'bool') {
    return (
      <Select id={id} value={value === 'false' ? 'false' : 'true'} onChange={(e) => onChange(e.target.value)} className="h-11 w-full">
        <option value="true">{t('config.yes')}</option>
        <option value="false">{t('config.no')}</option>
      </Select>
    )
  }
  if (item.kind === 'choice') {
    return (
      <Select id={id} value={value} onChange={(e) => onChange(e.target.value)} className="h-11 w-full">
        {(item.choices ?? []).map((c) => <option key={c} value={c}>{t(`config.choices.${i18nKey(item.key)}.${c}`, c)}</option>)}
      </Select>
    )
  }
  if (item.kind === 'time') return <Input id={id} type="time" value={value} onChange={(e) => onChange(e.target.value)} aria-label={label} />
  if (item.kind === 'timezone') {
    const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []
    return (
      <>
        <Input id={id} list={`${id}-list`} value={value} onChange={(e) => onChange(e.target.value)} aria-label={label} autoComplete="off" />
        <datalist id={`${id}-list`}>{zones.map((z) => <option key={z} value={z} />)}</datalist>
      </>
    )
  }
  const numeric = item.kind === 'int' || item.kind === 'number'
  return <Input id={id} value={value} onChange={(e) => onChange(e.target.value)} inputMode={numeric ? 'decimal' : undefined} aria-label={label} autoComplete="off" />
}
