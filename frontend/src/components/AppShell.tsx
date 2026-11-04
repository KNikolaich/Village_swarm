import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Bell, ChevronLeft, ChevronRight, Images, LayoutDashboard, LogOut, Network, NotebookPen, Settings, Thermometer, Workflow } from 'lucide-react'
import { NavLink, Outlet, useLocation } from 'react-router'
import { api, call } from '@/api/client'
import { keys } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'
import type { LiveState } from '@/lib/live'
import { cn } from '@/lib/utils'
import { useWideScreen } from '@/lib/layout'
import { loadSidebarCollapsed, saveSidebarCollapsed, sidebarCollapsed } from '@/lib/sidebar'
import { brandVersion } from '@/lib/version'
import { HoneycombLogo } from '@/components/HoneycombLogo'
import { ClusterBanner } from '@/components/ClusterBanner'
import { LanguageSwitch } from '@/components/LanguageSwitch'

// Labels are looked up when the shell renders, not when this file loads: the hive language is read before the
// first render, but after the modules are imported.
const nav = () => [
  { to: '/', label: t('nav.overview'), icon: LayoutDashboard, end: true },
  { to: '/climate', label: t('nav.climate'), icon: Thermometer, end: false },
  { to: '/rules', label: t('nav.rules'), icon: Workflow, end: false },
  { to: '/events', label: t('nav.events'), icon: Bell, end: false },
  { to: '/photos', label: t('nav.photos'), icon: Images, end: false },
  { to: '/notes', label: t('nav.notes'), icon: NotebookPen, end: false },
  { to: '/swarm', label: t('nav.swarm'), icon: Network, end: false },
  { to: '/settings', label: t('nav.settings'), icon: Settings, end: false },
]

/** Forms keep a readable width on a wide screen; the dashboards, lists and charts take all of it. */
const narrow = (path: string) => path.startsWith('/settings') || path.startsWith('/rules') || path === '/swarm/add'

function LiveDot({ state }: { state: LiveState }) {
  const color = state === 'connected' ? 'bg-emerald-400' : state === 'connecting' ? 'bg-amber-400 animate-pulse' : 'bg-red-500'
  return (
    <span className="flex items-center gap-2 text-xs text-neutral-400" title={t(`live.${state}`)}>
      <span className={cn('size-2 rounded-full', color)} />
      <span className="hidden sm:inline">{t(`live.${state}`)}</span>
    </span>
  )
}

/** The hive's version beside its name, small and gold: the same as Settings shows (one cached request). */
function HiveVersion({ className }: { className?: string }) {
  const version = useQuery({ queryKey: ['settings', 'version'], queryFn: () => call(api.GET('/api/settings/version')), staleTime: 60_000 })
  const shown = brandVersion(version.data?.version)
  if (!shown) return null
  return (
    <span className={cn('truncate font-mono text-[11px] text-amber-600/90', className)} title={t('settings.version').replace('{version}', version.data!.version)}>
      {shown}
    </span>
  )
}

/** Header + sidebar on desktop, bottom tab bar on the phone. */
export function AppShell({ live }: { live: LiveState }) {
  const qc = useQueryClient()
  const { pathname } = useLocation()
  const wide = useWideScreen()
  const [saved, setSaved] = useState(() => loadSidebarCollapsed())
  const [openedOnTablet, setOpenedOnTablet] = useState(false)
  // Narrowing to a tablet folds the bar again, even if it was opened there before.
  useEffect(() => setOpenedOnTablet(false), [wide])
  const collapsed = sidebarCollapsed(wide, saved, openedOnTablet)

  function toggleSidebar() {
    if (wide) {
      saveSidebarCollapsed(!saved)
      setSaved(!saved)
    } else setOpenedOnTablet(!openedOnTablet)
  }

  async function logout() {
    await api.POST('/api/auth/logout')
    // Drop everything the previous user saw, but keep the "me" query: App shows the login page when it is null.
    qc.removeQueries({ predicate: (q) => q.queryKey[0] !== keys.me[0] })
    qc.setQueryData(keys.me, null)
  }

  return (
    <div className="min-h-dvh md:flex">
      <aside
        className={cn(
          'sticky top-0 hidden h-dvh shrink-0 flex-col overflow-y-auto border-r border-neutral-800 py-4 transition-[width] duration-200 md:flex',
          collapsed ? 'w-16 px-2' : 'w-56 px-4',
        )}
      >
        {collapsed ? (
          <div className="mb-4 flex flex-col items-center gap-2">
            <HoneycombLogo className="size-7" />
            <button
              type="button"
              onClick={toggleSidebar}
              className="rounded-md p-1 text-neutral-500 hover:bg-neutral-900 hover:text-neutral-200"
              aria-label={t('nav.expand')}
              title={t('nav.expand')}
            >
              <ChevronRight className="size-4" />
            </button>
          </div>
        ) : (
          <div className="mb-6 flex items-center gap-2 pl-2">
            <HoneycombLogo className="size-7 shrink-0" />
            <span className="truncate font-semibold">{t('app.title')}</span>
            <HiveVersion />
            <button
              type="button"
              onClick={toggleSidebar}
              className="ml-auto shrink-0 rounded-md p-1 text-neutral-500 hover:bg-neutral-900 hover:text-neutral-200"
              aria-label={t('nav.collapse')}
              title={t('nav.collapse')}
            >
              <ChevronLeft className="size-4" />
            </button>
          </div>
        )}
        <nav className="space-y-1">
          {nav().map(({ to, label, icon: Icon, end }) => (
            <NavLink
              key={to}
              to={to}
              end={end}
              aria-label={collapsed ? label : undefined}
              title={collapsed ? label : undefined}
              className={({ isActive }) =>
                cn(
                  'flex items-center rounded-lg text-sm',
                  collapsed ? 'justify-center py-2.5' : 'gap-3 px-3 py-2',
                  isActive ? 'bg-neutral-800 text-neutral-50' : 'text-neutral-400 hover:bg-neutral-900',
                )
              }
            >
              <Icon className={collapsed ? 'size-5' : 'size-4'} />
              {!collapsed && label}
            </NavLink>
          ))}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-20 flex h-14 items-center justify-between border-b border-neutral-800 bg-neutral-950/90 px-4 backdrop-blur">
          <div className="flex items-center gap-2 md:hidden">
            <HoneycombLogo className="size-6" />
            <span className="font-semibold">{t('app.title')}</span>
            <HiveVersion className="hidden sm:inline" />
          </div>
          <div className="hidden md:block" />
          <div className="flex items-center gap-3">
            <LanguageSwitch />
            <LiveDot state={live} />
            <Button variant="ghost" size="icon" onClick={logout} aria-label={t('nav.logout')} title={t('nav.logout')}>
              <LogOut />
            </Button>
          </div>
        </header>

        <ClusterBanner />
        <main className={cn('mx-auto w-full flex-1 p-4 pb-24 md:pb-8', narrow(pathname) ? 'max-w-5xl' : 'max-w-5xl lg:max-w-none lg:px-6')}>
          <Outlet />
        </main>
      </div>

      <nav className="fixed inset-x-0 bottom-0 z-20 grid grid-cols-8 border-t border-neutral-800 bg-neutral-950/95 pb-[env(safe-area-inset-bottom)] backdrop-blur md:hidden">
        {nav().map(({ to, label, icon: Icon, end }) => (
          <NavLink
            key={to}
            to={to}
            end={end}
            className={({ isActive }) => cn('flex min-w-0 flex-col items-center gap-1 py-2 text-[10px] sm:text-xs', isActive ? 'text-amber-400' : 'text-neutral-500')}
          >
            <Icon className="size-5" />
            <span className="max-w-full truncate px-0.5">{label}</span>
          </NavLink>
        ))}
      </nav>
    </div>
  )
}
