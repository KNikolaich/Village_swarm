import { useInfiniteQuery, useMutation, useQueries, useQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { toast } from 'sonner'
import { t } from '@/i18n'
import { moveInTree } from '@/lib/notes'
import { api, call, type EventDto, type NoteNodeDto } from './client'

export const keys = {
  me: ['me'] as const,
  devices: ['devices'] as const,
  latest: ['telemetry', 'latest'] as const,
  monitoring: ['telemetry', 'monitoring'] as const,
  events: (filters: EventFilters) => ['events', filters] as const,
  eventsAll: ['events'] as const,
  mediaDay: (date: string, device?: string) => ['media', 'day', date, device ?? ''] as const,
  mediaDays: (from: string, to: string, device?: string) => ['media', 'days', from, to, device ?? ''] as const,
  otaRollout: ['firmware', 'rollout'] as const,
}

export type EventFilters = { type?: string; device?: string; severity?: string; limit?: number }

/** The firmware rollout on the hive ("update all outdated"): polled while it runs, so the page follows it. */
export const useOtaRollout = (enabled: boolean) =>
  useQuery({
    queryKey: keys.otaRollout,
    queryFn: () => call(api.GET('/api/firmware/rollout')),
    enabled,
    refetchInterval: (q) => (['running', 'stopping'].includes(q.state.data?.rollout?.state ?? '') ? 3000 : false),
  })

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

/** A camera's newest photo (its banner on a zone). */
export const useLatestPhoto = (device: string) =>
  useQuery({
    queryKey: ['media', 'latest', device] as const,
    queryFn: async () => (await call(api.GET('/api/media', { params: { query: { device, limit: 1 } } }))).items[0] ?? null,
    refetchInterval: 60_000,
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

/** The window a chart draws: up to this very moment, so readings pushed live land at its right edge. */
export function chartWindow(hours: number) {
  const to = new Date()
  return { from: new Date(to.getTime() - hours * 3_600_000), to }
}

/** Finer buckets on short windows (a few seconds in monitoring mode); the server's default on long ones. */
export const seriesPoints = (hours: number) => (hours <= 12 ? 360 : undefined)

export const useSeries = (device: string, metric: string, hours: number) =>
  useQuery({
    queryKey: ['telemetry', 'series', device, metric, hours],
    queryFn: () => {
      const { from, to } = historyWindow(hours)
      return call(api.GET('/api/telemetry/series', { params: { query: { device, metric, from: from.toISOString(), to: to.toISOString(), points: seriesPoints(hours) } } }))
    },
    refetchInterval: 5 * 60_000,
  })

/** Monitoring sessions in progress (frequent readings for a while, Hive.Modules.Telemetry.Monitoring). */
export const useMonitoring = () =>
  useQuery({ queryKey: keys.monitoring, queryFn: () => call(api.GET('/api/telemetry/monitoring')), refetchInterval: 30_000 })

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

/** How the overview is arranged on a wide screen and on a phone (null: the default order). */
export const useDashboardLayout = () =>
  useQuery({ queryKey: ['dashboard', 'layout'] as const, queryFn: () => call(api.GET('/api/dashboard/layout')), staleTime: 5 * 60_000 })

export const useZones = () =>
  useQuery({ queryKey: ['zones'] as const, queryFn: () => call(api.GET('/api/zones')) })

/** Several (device, metric) lines over one window: the probes of a bridge each get their own line on a zone chart. */
export const useMetricSeries = (lines: { device: string; metric: string }[], hours: number) =>
  useQueries({
    queries: lines.map(({ device, metric }) => ({
      queryKey: ['telemetry', 'series', device, metric, hours],
      queryFn: () => {
        const { from, to } = historyWindow(hours)
        return call(api.GET('/api/telemetry/series', { params: { query: { device, metric, from: from.toISOString(), to: to.toISOString(), points: seriesPoints(hours) } } }))
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

export const noteKeys = {
  tree: ['notes', 'tree'] as const,
  note: (id: number) => ['notes', 'note', id] as const,
  search: (q: string) => ['notes', 'search', q] as const,
  trash: ['notes', 'trash'] as const,
}

export const useNotesTree = () => useQuery({ queryKey: noteKeys.tree, queryFn: () => call(api.GET('/api/notes')) })

export const useNote = (id: number | undefined) =>
  useQuery({
    queryKey: noteKeys.note(id ?? 0),
    queryFn: () => call(api.GET('/api/notes/{id}', { params: { path: { id: id! } } })),
    enabled: id != null,
  })

export const useNoteSearch = (q: string) =>
  useQuery({
    queryKey: noteKeys.search(q),
    queryFn: () => call(api.GET('/api/notes/search', { params: { query: { q } } })),
    enabled: q.trim().length > 0,
    placeholderData: (previous) => previous,
  })

export const useNotesTrash = (enabled: boolean) =>
  useQuery({ queryKey: noteKeys.trash, queryFn: () => call(api.GET('/api/notes/trash')), enabled })

export function useCreateNote() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { title: string; parentId?: number | null; index?: number | null }) => call(api.POST('/api/notes', { body })),
    onSuccess: () => qc.invalidateQueries({ queryKey: noteKeys.tree }),
  })
}

/** Saves header fields or the text. The tree is patched in place, so typing a title does not refetch the whole tree. */
export function useUpdateNote() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, ...body }: { id: number; title?: string; icon?: string; color?: string; body?: string }) =>
      call(api.PATCH('/api/notes/{id}', { params: { path: { id } }, body })),
    onSuccess: (_, { id, title, icon, color }) => {
      qc.setQueryData<NoteNodeDto[]>(noteKeys.tree, (tree) =>
        tree?.map((n) =>
          n.id === id
            ? {
                ...n,
                title: title ?? n.title,
                icon: icon === undefined ? n.icon : icon || null,
                color: color === undefined ? n.color : color || null,
                updatedAt: new Date().toISOString(),
              }
            : n,
        ),
      )
    },
  })
}

/** Moves a branch; the tree is reordered locally first so the drop does not jump back while the server answers. */
export function useMoveNote() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, parentId, index }: { id: number; parentId: number | null; index: number }) =>
      call(api.POST('/api/notes/{id}/move', { params: { path: { id } }, body: { parentId, index } })),
    onMutate: ({ id, parentId, index }) => {
      const before = qc.getQueryData<NoteNodeDto[]>(noteKeys.tree)
      if (before) qc.setQueryData(noteKeys.tree, moveInTree(before, id, parentId, index))
      return { before }
    },
    onError: (_, __, context) => {
      if (context?.before) qc.setQueryData(noteKeys.tree, context.before)
    },
    onSettled: (_, __, { id }) => {
      void qc.invalidateQueries({ queryKey: noteKeys.tree })
      void qc.invalidateQueries({ queryKey: noteKeys.note(id) })
    },
  })
}

export function useDeleteNote() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => call(api.DELETE('/api/notes/{id}', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notes'] }),
  })
}

export function useRestoreNote() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => call(api.POST('/api/notes/{id}/restore', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notes'] }),
  })
}

/** Uploads a pasted or picked file as the raw request body. */
export async function uploadNoteAttachment(noteId: number, file: File | Blob, name?: string) {
  const fileName = name ?? (file instanceof File ? file.name : 'image.png')
  return call(
    api.POST('/api/notes/{id}/attachments', {
      params: { path: { id: noteId }, query: { name: fileName } },
      body: file as unknown as string,
      bodySerializer: (body) => body as unknown as Blob,
      headers: { 'Content-Type': file.type || 'application/octet-stream' },
    }),
  )
}

export function useDeleteNoteAttachment(noteId: number) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => call(api.DELETE('/api/notes/attachments/{id}', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: noteKeys.note(noteId) }),
  })
}
