import { useQueryClient } from '@tanstack/react-query'
import { Hexagon } from 'lucide-react'
import { useEffect } from 'react'
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router'
import { Toaster } from 'sonner'
import { ApiError, UNAUTHORIZED_EVENT } from '@/api/client'
import { keys, useMe } from '@/api/queries'
import { AppShell } from '@/components/AppShell'
import { useLive } from '@/lib/live'
import { EventsPage } from '@/pages/EventsPage'
import { LoginPage } from '@/pages/LoginPage'
import { OverviewPage } from '@/pages/OverviewPage'
import { PhotosPage } from '@/pages/PhotosPage'

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
      { path: 'events', element: <EventsPage /> },
      { path: 'photos', element: <PhotosPage /> },
      { path: 'photos/:date', element: <PhotosPage /> },
      { path: '*', element: <Navigate to="/" replace /> },
    ],
  },
])

export default function App() {
  const qc = useQueryClient()
  const me = useMe()

  // Any 401 (expired session) drops back to the login screen.
  useEffect(() => {
    const onUnauthorized = () => qc.setQueryData(keys.me, null)
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
  }, [qc])

  const loggedOut = me.data === null || (me.error instanceof ApiError && me.error.status === 401)

  return (
    <>
      {me.isLoading ? (
        <div className="flex min-h-dvh items-center justify-center">
          <Hexagon className="size-10 animate-pulse text-amber-400" />
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
