import { X } from 'lucide-react'
import { useEffect, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'

/** A sheet over the page: bottom sheet on a phone, centred box on a wide screen. Esc or a tap outside closes it. */
export function Dialog({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-black/60 sm:items-center" onClick={onClose}>
      <div
        role="dialog"
        aria-label={title}
        className="flex max-h-[85vh] w-full flex-col rounded-t-2xl border border-neutral-800 bg-neutral-900 pb-[env(safe-area-inset-bottom)] sm:max-w-md sm:rounded-2xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between gap-2 border-b border-neutral-800 px-4 py-3">
          <h2 className="min-w-0 truncate font-medium">{title}</h2>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label={t('notes.close')}><X /></Button>
        </div>
        <div className="overflow-y-auto p-2">{children}</div>
      </div>
    </div>
  )
}
