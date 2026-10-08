import { useQueryClient } from '@tanstack/react-query'
import { Bell, Hexagon, Images, LayoutDashboard, LogOut } from 'lucide-react'
import { NavLink, Outlet } from 'react-router'
import { api } from '@/api/client'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'
import type { LiveState } from '@/lib/live'
import { cn } from '@/lib/utils'

const nav = [
  { to: '/', label: t('nav.overview'), icon: LayoutDashboard, end: true },
  { to: '/events', label: t('nav.events'), icon: Bell, end: false },
  { to: '/photos', label: t('nav.photos'), icon: Images, end: false },
]

function LiveDot({ state }: { state: LiveState }) {
  const color = state === 'connected' ? 'bg-emerald-400' : state === 'connecting' ? 'bg-amber-400 animate-pulse' : 'bg-red-500'
  return (
    <span className="flex items-center gap-2 text-xs text-neutral-400" title={t(`live.${state}`)}>
      <span className={cn('size-2 rounded-full', color)} />
      <span className="hidden sm:inline">{t(`live.${state}`)}</span>
    </span>
  )
}

/** Header + sidebar on desktop, bottom tab bar on the phone. */
export function AppShell({ live }: { live: LiveState }) {
  const qc = useQueryClient()

  async function logout() {
    await api.POST('/api/auth/logout')
    qc.clear()
    await qc.invalidateQueries()
  }

  return (
    <div className="min-h-dvh md:flex">
      <aside className="hidden w-56 shrink-0 border-r border-neutral-800 p-4 md:block">
        <div className="mb-6 flex items-center gap-2 px-2">
          <Hexagon className="size-6 fill-amber-400/20 text-amber-400" />
          <span className="font-semibold">{t('app.title')}</span>
        </div>
        <nav className="space-y-1">
          {nav.map(({ to, label, icon: Icon, end }) => (
            <NavLink
              key={to}
              to={to}
              end={end}
              className={({ isActive }) =>
                cn('flex items-center gap-3 rounded-lg px-3 py-2 text-sm', isActive ? 'bg-neutral-800 text-neutral-50' : 'text-neutral-400 hover:bg-neutral-900')
              }
            >
              <Icon className="size-4" />
              {label}
            </NavLink>
          ))}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-20 flex h-14 items-center justify-between border-b border-neutral-800 bg-neutral-950/90 px-4 backdrop-blur">
          <div className="flex items-center gap-2 md:hidden">
            <Hexagon className="size-5 fill-amber-400/20 text-amber-400" />
            <span className="font-semibold">{t('app.title')}</span>
          </div>
          <div className="hidden md:block" />
          <div className="flex items-center gap-3">
            <LiveDot state={live} />
            <Button variant="ghost" size="icon" onClick={logout} aria-label={t('nav.logout')} title={t('nav.logout')}>
              <LogOut />
            </Button>
          </div>
        </header>

        <main className="mx-auto w-full max-w-5xl flex-1 p-4 pb-24 md:pb-8">
          <Outlet />
        </main>
      </div>

      <nav className="fixed inset-x-0 bottom-0 z-20 grid grid-cols-3 border-t border-neutral-800 bg-neutral-950/95 pb-[env(safe-area-inset-bottom)] backdrop-blur md:hidden">
        {nav.map(({ to, label, icon: Icon, end }) => (
          <NavLink
            key={to}
            to={to}
            end={end}
            className={({ isActive }) => cn('flex flex-col items-center gap-1 py-2 text-xs', isActive ? 'text-amber-400' : 'text-neutral-500')}
          >
            <Icon className="size-5" />
            {label}
          </NavLink>
        ))}
      </nav>
    </div>
  )
}
