import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useMe } from '@/api/queries'
import { getLanguage, t, type Language } from '@/i18n'
import { cn } from '@/lib/utils'

const options: { language: Language; flag: string; label: string }[] = [
  { language: 'en', flag: '🇬🇧', label: 'Eng' },
  { language: 'ru', flag: '🇷🇺', label: 'Ru' },
]

/** Header switch "🇬🇧 Eng | 🇷🇺 Ru". The language is one setting for the whole hive, so only admins change it. */
export function LanguageSwitch() {
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false

  async function change(language: Language) {
    if (language === getLanguage()) return
    if (!isAdmin) {
      toast.info(t('settings.languageAdminOnly'))
      return
    }
    try {
      await call(api.PUT('/api/settings/language', { body: { language } }))
      window.location.reload()
    } catch {
      toast.error(t('errors.generic'))
    }
  }

  return (
    <div className="flex items-center rounded-md border border-neutral-800 text-xs" role="group" aria-label={t('settings.language')}>
      {options.map((o, i) => (
        <button
          key={o.language}
          type="button"
          onClick={() => void change(o.language)}
          className={cn(
            'flex items-center gap-1 px-2 py-1',
            i > 0 && 'border-l border-neutral-800',
            o.language === getLanguage() ? 'bg-amber-400/15 text-amber-300' : 'text-neutral-400 hover:text-neutral-100',
          )}
        >
          <span aria-hidden>{o.flag}</span>
          {o.label}
        </button>
      ))}
    </div>
  )
}
