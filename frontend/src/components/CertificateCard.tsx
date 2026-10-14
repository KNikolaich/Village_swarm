import { Download, ShieldCheck } from 'lucide-react'
import { useState } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { cn } from '@/lib/utils'

const systems = ['windows', 'firefox', 'android', 'iphone', 'mac'] as const

/** Guess where the viewer is, so their own steps open first. */
function detect(): (typeof systems)[number] {
  const ua = navigator.userAgent
  if (/Firefox\//.test(ua)) return 'firefox'
  if (/Android/.test(ua)) return 'android'
  if (/iPhone|iPad/.test(ua)) return 'iphone'
  if (/Macintosh/.test(ua)) return 'mac'
  return 'windows'
}

/**
 * How to trust the hive's own certificate authority once per device (Caddy local_certs, deploy/caddy/Caddyfile),
 * so the browser stops warning about https://<hive>.
 */
export function CertificateCard() {
  const [system, setSystem] = useState(detect)
  const steps = t(`cert.steps.${system}`).split('|')
  return (
    <Card>
      <CardHeader><CardTitle>{t('cert.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-neutral-400">{t('cert.hint')}</p>
        <a href="/ca.crt" download="hive-ca.crt" className="inline-flex items-center gap-2 rounded-lg bg-amber-400 px-4 py-2 font-medium text-neutral-950 hover:bg-amber-300">
          <Download className="size-4" />{t('cert.download')}
        </a>
        <div className="flex flex-wrap gap-1">
          {systems.map((s) => (
            <button
              key={s}
              type="button"
              onClick={() => setSystem(s)}
              className={cn('rounded-lg border px-3 py-1 text-xs', s === system ? 'border-amber-400 text-amber-300' : 'border-neutral-700 text-neutral-400')}
            >
              {t(`cert.systems.${s}`)}
            </button>
          ))}
        </div>
        <ol className="list-decimal space-y-1 pl-5 text-neutral-300">
          {steps.map((step) => <li key={step}>{step}</li>)}
        </ol>
        <p className="flex items-start gap-2 text-xs text-neutral-500"><ShieldCheck className="mt-0.5 size-4 shrink-0" />{t('cert.once')}</p>
      </CardContent>
    </Card>
  )
}
