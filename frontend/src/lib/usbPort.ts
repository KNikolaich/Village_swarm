// What the USB setup pages share: the port the person picked (kept for a retry, so it is not asked for again) and what the page
// is doing while it waits for the board (shown as a line under the steps).
import { useRef, useState } from 'react'
import { t } from '@/i18n'
import type { ConsoleStatus } from '@/lib/hornetSetup'
import type { SerialPortLike } from '@/lib/serial'

/** The line for a status: "Попытка 2 из 4: перезапускаю плату по USB…". */
export function statusText(status: ConsoleStatus): string {
  return t(`usbSetup.status.${status.step}`).replace('{attempt}', String(status.attempt)).replace('{of}', String(status.of))
}

/** Asks the browser for a port; must run in a click. A closed picker rejects with NotFoundError. */
export async function requestPort(): Promise<SerialPortLike> {
  return (navigator as unknown as { serial: { requestPort(): Promise<SerialPortLike> } }).serial.requestPort()
}

/**
 * The port of the last try. A retry reuses it (the person already allowed it, and a Retry button that opens the picker
 * again is a second chance to pick the wrong one); "pick again" forgets it.
 */
export function usePickedPort() {
  const picked = useRef<SerialPortLike | null>(null)
  const [status, setStatus] = useState<ConsoleStatus | null>(null)
  return {
    status,
    setStatus,
    hasPort: () => picked.current !== null,
    /** The kept port when <reuse> and there is one, else the picker's choice (which is kept). */
    async port(reuse: boolean): Promise<SerialPortLike> {
      if (reuse && picked.current) return picked.current
      picked.current = await requestPort()
      return picked.current
    },
  }
}
