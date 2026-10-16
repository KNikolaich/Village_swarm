import { useInfiniteQuery, useMutation, useQueries, useQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { toast } from 'sonner'
import { t } from '@/i18n'
import { api, call, type EventDto } from './client'

export const keys = {
  me: ['me'] as const,
  devices: ['devices'] as const,
  latest: ['telemetry', 'latest'] as const,
  events: (filters: EventFilters) => ['events', filters] as const,
  eventsAll: ['events'] as const,
  mediaDay: (date: string, device?: string) => ['media', 'day', date, device ?? ''] as const,
  mediaDays: (from: string, to: string, device?: string) => ['media', 'days', from, to, device ?? ''] as const,
}

export type EventFilters = { type?: string; device?: string; severity?: string; limit?: number }

export const useMe = () =>
  useQuery({ queryKey: keys.me, queryFn: () => call(api.GET('/api/me')), retry: false, staleTime: 5 * 60_000 })

/**
 * The swarm. Removed hornets ("deceased", status disabled) stay in the registry with their history but are hidden
 * unless asked for: pickers and dashboards want the living, the swarm page, history and the console want everyone.
 */
export const useDevices = (withDeceased = false) =>
  useQuery({
    queryKey: keys.devices,
    queryFn: () => call(api.GET('/api/devices')),
    refetchInterval: 60_000,
    select: (all) => (withDeceased ? all : all.filter((d) => d.status !== 'disabled')),
  })

export const useLatestTelemetry = () =>
  useQuery({ queryKey: keys.latest, queryFn: () => call(api.GET('/api/telemetry/latest')), refetchInterval: 5 * 60_000 })

export const useEvents = (filters: EventFilters) =>
  useInfiniteQuery({
    queryKey: keys.events(filters),
    queryFn: ({ pageParam }) =>
      call(api.GET('/api/events', { params: { query: { ...filters, cursor: pageParam } } })),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  })

export const useMediaDay = (date: string, device?: string) =>
  useQuery({
    queryKey: keys.mediaDay(date, device),
    queryFn: async () => {
      // A day of photos fits in a few pages; the archive shows the whole day at once.
      const items = []
      let cursor: string | undefined
      do {
        const page = await call(api.GET('/api/media', { params: { query: { date, device, cursor, limit: 200 } } }))
        items.push(...page.items)
        cursor = page.nextCursor ?? undefined
      } while (cursor)
      return items
    },
  })

export const useMediaDays = (from: string, to: string, device?: string) =>
  useQuery({ queryKey: keys.mediaDays(from, to, device), queryFn: () => call(api.GET('/api/media/days', { params: { query: { from, to, device } } })) })

/** Replaces an event in every cached events list (after ack, or when its photos arrive). */
export function patchEventInCache(qc: ReturnType<typeof useQueryClient>, updated: EventDto) {
  qc.setQueriesData<InfiniteData<{ items: EventDto[]; nextCursor?: string | null }>>({ queryKey: keys.eventsAll }, (data) =>
    data && {
      ...data,
      pages: data.pages.map((p) => ({ ...p, items: p.items.map((e) => (e.id === updated.id ? updated : e)) })),
    },
  )
}

export function useAckEvent() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => call(api.POST('/api/events/{id}/ack', { params: { path: { id } } })),
    onSuccess: (event) => patchEventInCache(qc, event),
  })
}

/** "Просмотрено" for every unseen event of the filter (device, from, to), the same on the web and in Telegram. */
export function useAckAll() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (filter: { device?: string }) => call(api.POST('/api/events/ack', { body: filter })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.eventsAll }),
  })
}

export function usePin() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, pinned }: { id: string; pinned: boolean }) =>
      pinned
        ? call(api.POST('/api/media/{id}/pin', { params: { path: { id } } }))
        : call(api.DELETE('/api/media/{id}/pin', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['media'] }),
  })
}

export const useArmed = () =>
  useQuery({ queryKey: ['modes', 'armed'], queryFn: () => call(api.GET('/api/modes/armed')), refetchInterval: 60_000 })

export type GuardMode = 'disarmed' | 'armed' | 'arriving'

export function useSetArmed() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (mode: GuardMode) => call(api.PUT('/api/modes/armed', { body: { armed: mode !== 'disarmed', mode } })),
    onSuccess: (data) => qc.setQueryData(['modes', 'armed'], data),
  })
}

export const useTelegram = () =>
  useQuery({ queryKey: ['telegram'], queryFn: () => call(api.GET('/api/me/telegram')) })

/** A window of `hours` ending now, rounded to the minute so the query key stays stable between refreshes. */
export function historyWindow(hours: number) {
  const to = new Date(Math.floor(Date.now() / 60_000) * 60_000)
  return { from: new Date(to.getTime() - hours * 3_600_000), to }
}

export const useSeries = (device: string, metric: string, hours: number) =>
  useQuery({
    queryKey: ['telemetry', 'series', device, metric, hours],
    queryFn: () => {
      const { from, to } = historyWindow(hours)
      return call(api.GET('/api/telemetry/series', { params: { query: { device, metric, from: from.toISOString(), to: to.toISOString() } } }))
    },
    refetchInterval: 5 * 60_000,
  })

export const useHealthHistory = (device: string, hours: number) =>
  useQuery({
    queryKey: ['telemetry', 'health', device, hours],
    queryFn: () => {
      const { from, to } = historyWindow(hours)
      return call(api.GET('/api/telemetry/health', { params: { query: { device, from: from.toISOString(), to: to.toISOString() } } }))
    },
    refetchInterval: 5 * 60_000,
  })

export const useDeviceLogs = (device: string, level: string) =>
  useQuery({
    queryKey: ['telemetry', 'logs', device, level],
    queryFn: () => call(api.GET('/api/telemetry/logs', { params: { query: { device, level: level || undefined, limit: 30 } } })),
    refetchInterval: 30_000,
  })

export const useCommandHistory = (device: string, enabled: boolean) =>
  useQuery({
    queryKey: ['devices', device, 'commands'],
    queryFn: () => call(api.GET('/api/devices/{deviceId}/commands', { params: { path: { deviceId: device }, query: { limit: 10 } } })),
    enabled,
    refetchInterval: 30_000,
  })

export const useWeather = () =>
  useQuery({ queryKey: ['weather'] as const, queryFn: () => call(api.GET('/api/weather')), refetchInterval: 10 * 60_000 })

export const useZones = () =>
  useQuery({ queryKey: ['zones'] as const, queryFn: () => call(api.GET('/api/zones')) })

/** The same metric of several devices over one window (one line each on a zone chart). */
export const useDeviceSeries = (devices: string[], metric: string, hours: number) =>
  useQueries({
    queries: devices.map((device) => ({
      queryKey: ['telemetry', 'series', device, metric, hours],
      queryFn: () => {
        const { from, to } = historyWindow(hours)
        return call(api.GET('/api/telemetry/series', { params: { query: { device, metric, from: from.toISOString(), to: to.toISOString() } } }))
      },
      refetchInterval: 5 * 60_000,
    })),
  })

/** Sends a command to a hornet, with a toast for the outcome. */
export function useSendCommand(deviceId: string) {
  return async (name: string, payload?: Record<string, unknown>) => {
    try {
      await call(api.POST('/api/devices/{deviceId}/commands', { params: { path: { deviceId } }, body: { name, payload: payload as Record<string, never> | undefined } }))
      toast.success(t('device.commandSent'))
    } catch {
      toast.error(t('device.commandRefused'))
    }
  }
}

export const useRules = () =>
  useQuery({ queryKey: ['rules'] as const, queryFn: () => call(api.GET('/api/rules')) })

export const useRuleRuns = (id: number | null) =>
  useQuery({
    queryKey: ['rules', id, 'runs'] as const,
    queryFn: () => call(api.GET('/api/rules/{id}/runs', { params: { path: { id: id! }, query: { limit: 30 } } })),
    enabled: id != null,
    refetchInterval: 30_000,
  })

export const useBackups = (enabled: boolean) =>
  useQuery({ queryKey: ['backups'] as const, queryFn: () => call(api.GET('/api/backups')), enabled, refetchInterval: 5 * 60_000 })

export const useRetention = (enabled: boolean) =>
  useQuery({ queryKey: ['retention'] as const, queryFn: () => call(api.GET('/api/retention')), enabled, refetchInterval: 5 * 60_000 })
