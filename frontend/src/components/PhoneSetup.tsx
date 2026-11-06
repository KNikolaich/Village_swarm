import { HelpTip } from '@/components/HelpTip'
import { QrCode } from '@/components/QrCode'
import { t } from '@/i18n'
import { portalLink } from '@/lib/setupLink'

/**
 * The other way to set a remote monitor up: on the spot, from a phone. The code holds the address of the panel's own
 * settings page with the hive's address, the token and the Wi-Fi in it, so nothing is copied by hand from a screen.
 */
export function PhoneSetup({ url, token, ssid, pass, intervalMin }: { url: string; token: string; ssid?: string; pass?: string; intervalMin?: number }) {
  const link = portalLink({ url, token, ssid, pass, intervalMin })
  return (
    <details className="text-xs text-neutral-500">
      <summary className="cursor-pointer">{t('phoneSetup.title')}</summary>
      <div className="mt-2 flex flex-wrap items-start gap-3">
        <QrCode text={link} label={t('phoneSetup.qr')} size={176} />
        <div className="min-w-0 flex-1 basis-48 space-y-1">
          <p className="flex items-start gap-1">
            <span>{t('phoneSetup.steps')}</span>
            <HelpTip text={t('phoneSetup.help')} />
          </p>
          <p className="break-all text-neutral-400">{t('phoneSetup.token')}: <code className="text-neutral-200">{token}</code></p>
        </div>
      </div>
    </details>
  )
}
