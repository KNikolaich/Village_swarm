import { ChevronDown } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'
import { api, call, errorText } from '@/api/client'
import { useMe } from '@/api/queries'
import { getLanguage, t, type Language } from '@/i18n'
import { Flag } from '@/components/Flag'
import { cn } from '@/lib/utils'

const options: { language: Language; code: string; name: string }[] = [
  { language: 'en', code: 'Eng', name: 'English' },
  { language: 'ru', code: 'Ru', name: 'Русский' },
]

/** Header language picker: the current flag and code, and a list of the languages on press (the same on every screen). The language is one setting for the whole hive, so only admins change it. */
export function LanguageSwitch() {
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const current = options.find((o) => o.language === getLanguage()) ?? options[0]

  useEffect(() => {
    if (!open) return
    const onDown = (e: PointerEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('pointerdown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('pointerdown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  async function change(language: Language) {
    setOpen(false)
    if (language === getLanguage()) return
    if (!isAdmin) {
      toast.info(t('settings.languageAdminOnly'))
      return
    }
    try {
      await call(api.PUT('/api/settings/language', { body: { language } }))
      window.location.reload()
    } catch (e) {
      toast.error(errorText(e, t('errors.generic')))
    }
  }

  return (
    <div ref={root} className="relative">
      <button
        type="button"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={t('settings.language')}
        onClick={() => setOpen(!open)}
        className="flex h-8 items-center gap-1.5 rounded-md border border-neutral-800 px-2 text-xs text-neutral-200 hover:bg-neutral-900"
      >
        <Flag language={current.language} />
        {current.code}
        <ChevronDown className={cn('size-3.5 text-neutral-500 transition-transform', open && 'rotate-180')} />
      </button>
      {open && (
        <ul role="listbox" aria-label={t('settings.language')} className="absolute right-0 top-full z-40 mt-1 min-w-36 overflow-hidden rounded-md border border-neutral-800 bg-neutral-900 py-1 shadow-xl">
          {options.map((o) => (
            <li key={o.language} role="option" aria-selected={o.language === current.language}>
              <button
                type="button"
                onClick={() => void change(o.language)}
                className={cn('flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-neutral-800', o.language === current.language ? 'text-amber-300' : 'text-neutral-200')}
              >
                <Flag language={o.language} />
                <span className="w-8 text-xs">{o.code}</span>
                <span className="text-neutral-400">{o.name}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
