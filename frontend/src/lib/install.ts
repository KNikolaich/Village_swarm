import { useSyncExternalStore } from 'react'

/** Chrome's install offer (not in the DOM typings). */
interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

// The offer can come before any page that shows the button is open: keep it from the start (imported in main.tsx).
let offer: BeforeInstallPromptEvent | null = null
const listeners = new Set<() => void>()
const notify = () => listeners.forEach((l) => l())

window.addEventListener('beforeinstallprompt', (e) => {
  offer = e as BeforeInstallPromptEvent
  notify()
})
window.addEventListener('appinstalled', () => {
  offer = null
  notify()
})

const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function isInstalled() {
  return window.matchMedia('(display-mode: standalone)').matches || (navigator as { standalone?: boolean }).standalone === true
}

export const isIos = () => /iPhone|iPad|iPod/.test(navigator.userAgent)

/** The pending install offer, or null when the browser has not made one (installed already, iOS, no trusted HTTPS). */
export function useInstallOffer() {
  return useSyncExternalStore(subscribe, () => offer)
}

export async function install() {
  if (!offer) return false
  await offer.prompt()
  const { outcome } = await offer.userChoice
  offer = null
  notify()
  return outcome === 'accepted'
}
