import { Cpu } from 'lucide-react'
import { createElement, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'
import { translateEspWebTools } from '@/lib/espWebToolsRu'
import { serialSupported } from '@/lib/serial'

/**
 * The "Flash" button of ESP Web Tools for one firmware build (an env like guard-cam-esp32cam): the browser writes the
 * image over USB. The component is loaded on demand; when it cannot be (a page opened before the hive was updated asks for
 * a file that is gone), the reason is shown instead of leaving the button out.
 */
export function FlashButton({ build }: { build: string }) {
  const [ready, setReady] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const supported = serialSupported()

  useEffect(() => {
    if (!supported) return
    translateEspWebTools()
    // ESP Web Tools (a web component, ~100 KB) is only loaded where flashing is offered.
    import('esp-web-tools/dist/web/install-button.js').then(() => setReady(true), (e: unknown) => setLoadError(String(e)))
  }, [supported])

  if (!supported) return <p className="text-amber-300">{t('add.noSerial')}</p>
  if (loadError)
    return (
      <p className="space-y-1 text-amber-300">
        <span className="block">{t('add.flashLoadFailed')}</span>
        <span className="block break-all text-xs text-neutral-500">{loadError}</span>
        <Button variant="secondary" size="sm" onClick={() => window.location.reload()}>{t('add.reloadPage')}</Button>
      </p>
    )
  if (!ready) return null
  return createElement('esp-web-install-button', { key: build, manifest: `/api/firmware/${build}/manifest.json` },
    createElement(Button, { slot: 'activate' } as never, createElement(Cpu), t('add.flash')))
}
