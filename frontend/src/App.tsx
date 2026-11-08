import { useQueryClient } from '@tanstack/react-query'
import { lazy, Suspense, useEffect } from 'react'
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router'
import { Toaster } from 'sonner'
import { ApiError, UNAUTHORIZED_EVENT } from '@/api/client'
import { keys, useMe } from '@/api/queries'
import { AppShell } from '@/components/AppShell'
import { HoneycombLogo } from '@/components/HoneycombLogo'
import { useLive } from '@/lib/live'
import { DevicePage } from '@/pages/DevicePage'
import { ClimatePage } from '@/pages/ClimatePage'
import { ConsolePage } from '@/pages/ConsolePage'
import { EventsPage } from '@/pages/EventsPage'
import { RuleEditorPage } from '@/pages/RuleEditorPage'
import { RulesPage } from '@/pages/RulesPage'
import { LoginPage } from '@/pages/LoginPage'
import { OverviewPage } from '@/pages/OverviewPage'
import { PhotosPage } from '@/pages/PhotosPage'
import { AddHornetPage } from '@/pages/AddHornetPage'
import { MonitorPage } from '@/pages/MonitorPage'
import { SettingsPage } from '@/pages/SettingsPage'
import { SwarmPage } from '@/pages/SwarmPage'
import { t } from '@/i18n'
import { Skeleton } from '@/components/ui/primitives'

// The Markdown editor is heavy: notes load on their own, the rest of the app stays light on 4G.
const NotesPage = lazy(() =>
  import('@/pages/NotesPage').then(
    (m) => ({ default: m.NotesPage }),
    // After an update the page in the browser still names files of the old build: one reload brings the new ones.
    (error: unknown) => {
      if (reloadOnce()) return new Promise<never>(() => undefined)
      throw error
    },
  ),
)
const notes = (
  <Suspense fallback={<Skeleton className="h-96" />}>
    <NotesPage />
  </Suspense>
)

/** true: the page is being reloaded (once per minute at most, so a real outage does not loop). */
function reloadOnce(): boolean {
  try {
    const last = Number(sessionStorage.getItem('chunk.reload') ?? 0)
    if (Date.now() - last < 60_000) return false
    sessionStorage.setItem('chunk.reload', String(Date.now()))
  } catch {
    return false
  }
  window.location.reload()
  return true
}

/** A failed page inside the layout: the menu stays, the message says what to do. */
function RouteError() {
  return (
    <div className="space-y-3 py-6">
      <p className="text-sm text-neutral-300">{t('errors.pageFailed')}</p>
      <button type="button" className="rounded-md border border-neutral-700 px-3 py-1.5 text-sm hover:bg-neutral-900" onClick={() => window.location.reload()}>
        {t('errors.reload')}
      </button>
    </div>
  )
}

function Authenticated() {
  const live = useLive(true)
  return <AppShell live={live} />
}

const router = createBrowserRouter([
  {
    path: '/',
    element: <Authenticated />,
    children: [{ errorElement: <RouteError />, children: [
      { index: true, element: <OverviewPage /> },
      { path: 'climate', element: <ClimatePage /> },
      { path: 'rules', element: <RulesPage /> },
      { path: 'rules/:id', element: <RuleEditorPage /> },
      { path: 'events', element: <EventsPage /> },
      { path: 'photos', element: <PhotosPage /> },
      { path: 'photos/:date', element: <PhotosPage /> },
      { path: 'swarm', element: <SwarmPage /> },
      { path: 'swarm/add', element: <AddHornetPage /> },
      { path: 'console', element: <ConsolePage /> },
      { path: 'swarm/:deviceId', element: <DevicePage /> },
      { path: 'settings', element: <SettingsPage /> },
      { path: 'monitor', element: <MonitorPage /> },
      { path: 'notes', element: notes },
      { path: 'notes/:id', element: notes },
      { path: '*', element: <Navigate to="/" replace /> },
    ] }],
  },
])

export default function App() {
  const qc = useQueryClient()
  const me = useMe()

  // Any 401 (expired session) drops back to the login screen.
  useEffect(() => {
    // One 401 is not proof the session is gone (a single request can be refused for its own reason): re-ask
    // "who am I" instead, and the login screen comes only if that answer is 401 too.
    const onUnauthorized = () => void qc.invalidateQueries({ queryKey: keys.me })
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
  }, [qc])

  const loggedOut = me.data === null || (me.error instanceof ApiError && me.error.status === 401)

  return (
    <>
      {me.isLoading ? (
        <div className="flex min-h-dvh items-center justify-center">
          <HoneycombLogo className="size-12 animate-pulse" />
        </div>
      ) : loggedOut || !me.data ? (
        <LoginPage />
      ) : (
        <RouterProvider router={router} />
      )}
      <Toaster theme="dark" position="top-center" richColors />
    </>
  )
}
