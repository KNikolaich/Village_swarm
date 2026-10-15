import { cn } from '@/lib/utils'

/** The hive's mark: three honeycomb cells, the same picture as the favicon and the PWA icons (public/favicon.svg). */
export function HoneycombLogo({ className }: { className?: string }) {
  return <img src="/favicon.svg" alt="" aria-hidden="true" draggable={false} className={cn('shrink-0 select-none', className)} />
}
