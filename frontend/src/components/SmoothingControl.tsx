import { Ellipsis, Plus, Trash2, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { addTemplate, maxWindow, removeTemplate, smoothKinds, templateName, updateTemplate, type SmoothKind } from '@/lib/smoothing'
import { useSmoothingState } from '@/lib/smoothingStore'

/** The smoothing setting of every chart in one row: a tick box, the template list and «⋯» to add, edit and delete templates. */
export function SmoothingControl() {
  const [state, update] = useSmoothingState()
  const [editing, setEditing] = useState(false)
  const none = state.templates.length === 0

  return (
    <div className="flex items-center gap-1">
      <label className="flex cursor-pointer select-none items-center gap-1.5 text-xs text-neutral-400" title={t('smoothing.toggle')}>
        <input
          type="checkbox"
          className="size-3.5 accent-amber-400"
          checked={state.enabled && !none}
          disabled={none}
          onChange={(e) => update((s) => ({ ...s, enabled: e.target.checked }))}
        />
        {t('smoothing.label')}
      </label>
      <Select
        value={state.selected ?? ''}
        disabled={none}
        onChange={(e) => update((s) => ({ ...s, selected: e.target.value, enabled: true }))}
        className="h-8 w-auto text-xs"
        aria-label={t('smoothing.template')}
      >
        {none && <option value="">—</option>}
        {state.templates.map((tpl) => <option key={tpl.id} value={tpl.id}>{templateName(tpl)}</option>)}
      </Select>
      <Button variant="ghost" size="icon" title={t('smoothing.edit')} aria-label={t('smoothing.edit')} onClick={() => setEditing(true)}><Ellipsis /></Button>
      {editing && <TemplateEditor onClose={() => setEditing(false)} />}
    </div>
  )
}

function TemplateEditor({ onClose }: { onClose: () => void }) {
  const [state, update] = useSmoothingState()

  // Escape closes only this window, not the chart window behind it.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      e.stopPropagation()
      onClose()
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [onClose])

  return (
    <div role="dialog" aria-modal="true" aria-label={t('smoothing.editTitle')} className="fixed inset-0 z-[60] flex items-center justify-center bg-black/70 p-4" onClick={(e) => { e.stopPropagation(); onClose() }}>
      <div className="w-full max-w-sm space-y-3 rounded-xl border border-neutral-800 bg-neutral-950 p-4 shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center gap-2">
          <p className="min-w-0 flex-1 font-semibold">{t('smoothing.editTitle')}</p>
          <Button variant="ghost" size="icon" title={t('tileChart.close')} aria-label={t('tileChart.close')} onClick={onClose}><X /></Button>
        </div>
        <p className="text-xs text-neutral-500">{t('smoothing.hint')}</p>
        <div className="space-y-2">
          {state.templates.length === 0 && <p className="text-sm text-neutral-500">{t('smoothing.none')}</p>}
          {state.templates.map((tpl) => (
            <div key={tpl.id} className="flex items-center gap-2">
              <Select
                value={tpl.kind}
                onChange={(e) => update((s) => updateTemplate(s, tpl.id, { kind: e.target.value as SmoothKind }))}
                className="h-8 min-w-0 flex-1 text-xs"
                aria-label={t('smoothing.kind')}
              >
                {smoothKinds.map((k) => <option key={k} value={k}>{t(`smoothing.kinds.${k}`)}</option>)}
              </Select>
              <Input
                type="number"
                min={1}
                max={maxWindow}
                value={tpl.n}
                onChange={(e) => update((s) => updateTemplate(s, tpl.id, { n: Number(e.target.value) }))}
                className="h-8 w-16 text-xs"
                aria-label={t('smoothing.window')}
              />
              <span className="w-24 shrink-0 truncate text-xs tabular-nums text-neutral-400">{templateName(tpl)}</span>
              <Button variant="ghost" size="icon" title={t('smoothing.delete')} aria-label={t('smoothing.delete')} onClick={() => update((s) => removeTemplate(s, tpl.id))}><Trash2 /></Button>
            </div>
          ))}
        </div>
        <Button variant="outline" size="sm" onClick={() => update((s) => addTemplate(s))}><Plus />{t('smoothing.add')}</Button>
      </div>
    </div>
  )
}
