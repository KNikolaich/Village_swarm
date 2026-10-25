import { CircleHelp } from 'lucide-react'
import { useState } from 'react'
import { t } from '@/i18n'
import { cn } from '@/lib/utils'

/** A (?) next to a setting: tap to read how to set it, tap the text to close it. */
export function HelpTip({ text }: { text: string }) {
  const [open, setOpen] = useState(false)
  return (
    <span className="relative inline-flex">
      <button type="button" title={text} aria-label={t('display.helpLabel')} aria-expanded={open} onClick={() => setOpen(!open)}
        className={cn('inline-flex align-middle', open ? 'text-amber-400' : 'text-neutral-500 hover:text-neutral-300')}>
        <CircleHelp className="size-3.5" />
      </button>
      {open && (
        <span role="tooltip" onClick={() => setOpen(false)}
          className="fixed inset-x-4 top-24 z-50 block whitespace-pre-line rounded-lg border border-neutral-700 bg-neutral-900 p-3 text-xs font-normal text-neutral-300 shadow-xl sm:absolute sm:inset-x-auto sm:left-0 sm:top-6 sm:z-20 sm:w-80">
          {text}
        </span>
      )}
    </span>
  )
}
