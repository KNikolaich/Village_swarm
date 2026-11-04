import type { Language } from '@/i18n'

/**
 * A flag drawn as a picture: Windows has no flag emoji (they show as the letters "GB"), so the same flag has to come
 * from the page itself to look the same on every device.
 */
export function Flag({ language, className }: { language: Language; className?: string }) {
  return (
    <svg viewBox="0 0 60 40" aria-hidden className={className ?? 'h-4 w-6 rounded-[3px]'} role="img">
      {language === 'ru' ? (
        <>
          <rect width="60" height="13.4" fill="#fff" />
          <rect y="13.3" width="60" height="13.4" fill="#0039a6" />
          <rect y="26.6" width="60" height="13.4" fill="#d52b1e" />
        </>
      ) : (
        <>
          <rect width="60" height="40" fill="#012169" />
          <path d="M0 0 60 40M60 0 0 40" stroke="#fff" strokeWidth="8" />
          <path d="M0 0 60 40M60 0 0 40" stroke="#c8102e" strokeWidth="3" />
          <path d="M30 0v40M0 20h60" stroke="#fff" strokeWidth="13" />
          <path d="M30 0v40M0 20h60" stroke="#c8102e" strokeWidth="7.5" />
        </>
      )}
    </svg>
  )
}
