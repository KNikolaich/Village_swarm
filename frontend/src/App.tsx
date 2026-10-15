import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router'
import { Toaster } from 'sonner'
import { ApiError, UNAUTHORIZED_EVENT } from '@/api/client'
import { keys, useMe } from '@/api/queries'
import { AppShell } from '@/components/AppShell'
import { HoneycombLogo } from '@/components/HoneycombLogo'
import { useLive } from '@/lib/live'
import { DevicePage } from '@/pages/DevicePage'
import { ClimatePage } from '@/pages/ClimatePage'
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

function Authenticated() {
  const live = useLive(true)
  return <AppShell live={live} />
}

const router = createBrowserRouter([
  {
    path: '/',
    element: <Authenticated />,
    children: [
      { index: true, element: <OverviewPage /> },
      { path: 'climate', element: <ClimatePage /> },
      { path: 'rules', element: <RulesPage /> },
      { path: 'rules/:id', element: <RuleEditorPage /> },
      { path: 'events', element: <EventsPage /> },
      { path: 'photos', element: <PhotosPage /> },
      { path: 'photos/:date', element: <PhotosPage /> },
      { path: 'swarm', element: <SwarmPage /> },
      { path: 'swarm/add', element: <AddHornetPage /> },
      { path: 'swarm/:deviceId', element: <DevicePage /> },
      { path: 'settings', element: <SettingsPage /> },
      { path: 'monitor', element: <MonitorPage /> },
      { path: '*', element: <Navigate to="/" replace /> },
    ],
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
