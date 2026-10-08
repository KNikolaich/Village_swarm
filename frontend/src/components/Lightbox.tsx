import { ChevronLeft, ChevronRight, Download, Pin, PinOff, X } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { usePin } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'

export type LightboxPhoto = { id: string; url: string; thumbUrl: string; caption?: string; pinned?: boolean }

/** Full-screen viewer: arrows, swipe, Esc; download and "keep forever" (spec 8.2 screen 3). */
export function Lightbox({ photos, index, onIndex, onClose }: {
  photos: LightboxPhoto[]
  index: number
  onIndex: (i: number) => void
  onClose: () => void
}) {
  const pin = usePin()
  const photo = photos[index]
  const [pinned, setPinned] = useState(photo?.pinned ?? false)
  const touchX = useRef<number | null>(null)

  useEffect(() => setPinned(photo?.pinned ?? false), [photo])

  const go = useCallback((delta: number) => onIndex((index + delta + photos.length) % photos.length), [index, photos.length, onIndex])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      if (e.key === 'ArrowLeft') go(-1)
      if (e.key === 'ArrowRight') go(1)
    }
    window.addEventListener('keydown', onKey)
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      window.removeEventListener('keydown', onKey)
      document.body.style.overflow = overflow
    }
  }, [go, onClose])

  if (!photo) return null

  return (
    <div
      role="dialog"
      aria-modal="true"
      className="fixed inset-0 z-50 flex flex-col bg-black/95"
      onTouchStart={(e) => (touchX.current = e.touches[0].clientX)}
      onTouchEnd={(e) => {
        if (touchX.current === null) return
        const dx = e.changedTouches[0].clientX - touchX.current
        if (Math.abs(dx) > 50) go(dx < 0 ? 1 : -1)
        touchX.current = null
      }}
    >
      <div className="flex items-center justify-between gap-2 p-3 text-sm text-neutral-300">
        <span className="truncate">{[photo.caption, photos.length > 1 && `${index + 1}/${photos.length}`].filter(Boolean).join(' · ')}</span>
        <div className="flex shrink-0 items-center gap-1">
          <Button
            variant="ghost"
            size="icon"
            title={pinned ? t('photos.unpin') : t('photos.pin')}
            disabled={pin.isPending}
            onClick={() => pin.mutate({ id: photo.id, pinned: !pinned }, { onSuccess: () => setPinned(!pinned) })}
            className={pinned ? 'text-amber-400' : undefined}
          >
            {pinned ? <PinOff /> : <Pin />}
          </Button>
          <a href={photo.url} download={`${photo.id}.jpg`} title={t('photos.download')}>
            <Button variant="ghost" size="icon" tabIndex={-1}><Download /></Button>
          </a>
          <Button variant="ghost" size="icon" onClick={onClose} title={t('photos.close')}><X /></Button>
        </div>
      </div>
      <div className="relative flex min-h-0 flex-1 items-center justify-center p-2">
        <img src={photo.url} alt="" className="max-h-full max-w-full object-contain" />
        {photos.length > 1 && (
          <>
            <Button variant="ghost" size="icon" className="absolute left-2 hidden sm:flex" onClick={() => go(-1)}><ChevronLeft /></Button>
            <Button variant="ghost" size="icon" className="absolute right-2 hidden sm:flex" onClick={() => go(1)}><ChevronRight /></Button>
          </>
        )}
      </div>
      {pinned && <p className="pb-3 text-center text-xs text-amber-400">{t('photos.pinned')}</p>}
    </div>
  )
}
