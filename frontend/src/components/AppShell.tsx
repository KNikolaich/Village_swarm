import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Bell, ChevronLeft, ChevronRight, Images, LayoutDashboard, Network, NotebookPen, Thermometer, Workflow } from 'lucide-react'
import { NavLink, Outlet, useLocation } from 'react-router'
import { call, api } from '@/api/client'
import { useCluster } from '@/api/queries'
import { t } from '@/i18n'
import type { LiveState } from '@/lib/live'
import { cn } from '@/lib/utils'
import { useWideScreen } from '@/lib/layout'
import { loadSidebarCollapsed, saveSidebarCollapsed, sidebarCollapsed } from '@/lib/sidebar'
import { brandLabel } from '@/lib/version'
import { GlobalSettingsIcon } from '@/components/GlobalSettingsIcon'
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
  { to: '/settings', label: t('nav.settings'), icon: GlobalSettingsIcon, end: false },
]

/** Reachable from the header on a phone instead of the bottom bar, so the bar keeps room for the main screens. */
const headerOnPhone = ['/events', '/photos', '/settings']
/** The swarm is the big round button in the middle of the phone's bottom bar. */
const centerOnPhone = '/swarm'
const sidebarItems = () => nav()
const bottomItems = () => nav().filter((n) => !headerOnPhone.includes(n.to) && n.to !== centerOnPhone)

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

/** What the hive is called in the menu: its alias ("Дача"), or the word "Улей". */
function useHiveTitle(): string {
  const cluster = useCluster()
  return cluster.data?.alias || t('app.title')
}

/** The hive's version beside its name, small and gold: the same as Settings shows (one cached request). */
function HiveVersion({ className }: { className?: string }) {
  const version = useQuery({ queryKey: ['settings', 'version'], queryFn: () => call(api.GET('/api/settings/version')), staleTime: 60_000 })
  const label = brandLabel(version.data?.version, import.meta.env.VITE_HIVE_VERSION as string | undefined)
  if (label.stale)
    return (
      <button type="button" className={cn('truncate font-mono text-[11px] text-amber-400 underline decoration-dotted', className)}
        title={t('settings.pageStale')} onClick={() => window.location.reload()}>
        {label.text} ?
      </button>
    )
  return (
    <span className={cn('truncate font-mono text-[11px] text-amber-600/90', className)} title={t('settings.version').replace('{version}', label.text)}>
      {label.text}
    </span>
  )
}

/** One item of the phone's bottom bar. */
function BottomLink({ to, label, icon: Icon, end }: { to: string; label: string; icon: React.ComponentType<{ className?: string }>; end: boolean }) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) => cn('flex min-w-0 flex-col items-center gap-1 py-2 text-[10px] sm:text-xs', isActive ? 'text-amber-400' : 'text-neutral-500')}
    >
      <Icon className="size-5" />
      <span className="max-w-full truncate px-0.5">{label}</span>
    </NavLink>
  )
}

/** An icon-only link in the header: amber while its screen is open. */
function HeaderLink({ to, label, icon: Icon, className }: { to: string; label: string; icon: React.ComponentType<{ className?: string }>; className?: string }) {
  return (
    <NavLink
      to={to}
      aria-label={label}
      title={label}
      className={({ isActive }) =>
        cn('flex size-9 items-center justify-center rounded-md hover:bg-neutral-900', isActive ? 'text-amber-400' : 'text-neutral-400 hover:text-neutral-100', className)
      }
    >
      <Icon className="size-5" />
    </NavLink>
  )
}

/** Header + sidebar on desktop, bottom tab bar on the phone. */
export function AppShell({ live }: { live: LiveState }) {
  const { pathname } = useLocation()
  const title = useHiveTitle()
  // The tab is named after the hive ("Дача", "Город"), so tabs of two hives are told apart without opening them.
  useEffect(() => {
    document.title = title
  }, [title])
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
            <span className="truncate font-semibold">{title}</span>
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
          {sidebarItems().map(({ to, label, icon: Icon, end }) => (
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
          <div className="flex min-w-0 items-center gap-1 md:hidden">
            <HoneycombLogo className="size-6 shrink-0" />
            <span className="ml-1 mr-1 truncate font-semibold">{title}</span>
            <HiveVersion className="hidden sm:inline" />
            {/* Events and photos: icons only, the bottom bar is for the main screens. */}
            <HeaderLink to="/events" label={t('nav.events')} icon={Bell} />
            <HeaderLink to="/photos" label={t('nav.photos')} icon={Images} />
          </div>
          <div className="hidden md:block" />
          <div className="flex shrink-0 items-center gap-3">
            <LanguageSwitch />
            <LiveDot state={live} />
            <HeaderLink to="/settings" label={t('nav.settings')} icon={GlobalSettingsIcon} className="md:hidden" />
          </div>
        </header>

        <ClusterBanner />
        <main className={cn('mx-auto w-full flex-1 p-4 pb-24 md:pb-8', narrow(pathname) ? 'max-w-5xl' : 'max-w-5xl lg:max-w-none lg:px-6')}>
          <Outlet />
        </main>
      </div>

      <nav className="fixed inset-x-0 bottom-0 z-20 grid grid-cols-5 border-t border-neutral-800 bg-neutral-950/95 pb-[env(safe-area-inset-bottom)] backdrop-blur md:hidden">
        {bottomItems().slice(0, 2).map((item) => <BottomLink key={item.to} {...item} />)}
        <NavLink
          to={centerOnPhone}
          aria-label={t('nav.swarm')}
          title={t('nav.swarm')}
          className={({ isActive }) =>
            cn(
              '-mt-6 mb-2 flex size-16 items-center justify-center place-self-center rounded-full border-4 border-neutral-950 shadow-lg shadow-black/50',
              isActive ? 'bg-amber-400 text-neutral-950' : 'bg-neutral-800 text-amber-400 hover:bg-neutral-700',
            )
          }
        >
          <Network className="size-7" />
        </NavLink>
        {bottomItems().slice(2).map((item) => <BottomLink key={item.to} {...item} />)}
      </nav>
    </div>
  )
}
