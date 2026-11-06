import { useState } from 'react'
import { toast } from 'sonner'
import { Input } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { cn } from '@/lib/utils'

/** A place in a list (0..9999, lower first), saved when the field is left or Enter is pressed. */
export function SortInput({ value, onSave, label, className }: { value: number; onSave: (sort: number) => Promise<void>; label: string; className?: string }) {
  const [draft, setDraft] = useState<string | null>(null)

  async function commit() {
    if (draft === null) return
    const text = draft.trim()
    setDraft(null)
    if (text === '' || Number(text) === value) return
    if (!/^\d{1,4}$/.test(text)) {
      toast.error(t('swarm.sortInvalid'))
      return
    }
    try {
      await onSave(Number(text))
    } catch {
      toast.error(t('swarm.sortFailed'))
    }
  }

  return (
    <Input
      inputMode="numeric"
      aria-label={label}
      title={t('swarm.sortHint')}
      value={draft ?? String(value)}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => void commit()}
      onKeyDown={(e) => {
        if (e.key === 'Enter') e.currentTarget.blur()
        if (e.key === 'Escape') setDraft(null)
      }}
      className={cn('h-8 w-20 text-xs tabular-nums', className)}
    />
  )
}
