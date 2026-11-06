import { t } from '@/i18n'

/** Firefox, Safari and phone browsers have no Web Serial: say so instead of leaving the USB steps silently dead. */
export function SerialNotice() {
  return (
    <p role="alert" className="rounded border border-amber-500/40 bg-amber-500/10 p-2 text-amber-300">
      {t('add.noSerial')}
    </p>
  )
}
