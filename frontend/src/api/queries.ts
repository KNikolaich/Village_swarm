import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { api, call, type EventDto } from './client'

export const keys = {
  me: ['me'] as const,
  devices: ['devices'] as const,
  latest: ['telemetry', 'latest'] as const,
  events: (filters: EventFilters) => ['events', filters] as const,
  eventsAll: ['events'] as const,
  mediaDay: (date: string) => ['media', 'day', date] as const,
  mediaDays: (from: string, to: string) => ['media', 'days', from, to] as const,
}

export type EventFilters = { type?: string; device?: string; severity?: string; limit?: number }

export const useMe = () =>
  useQuery({ queryKey: keys.me, queryFn: () => call(api.GET('/api/me')), retry: false, staleTime: 5 * 60_000 })

export const useDevices = () =>
  useQuery({ queryKey: keys.devices, queryFn: () => call(api.GET('/api/devices')), refetchInterval: 60_000 })

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

export const useMediaDay = (date: string) =>
  useQuery({
    queryKey: keys.mediaDay(date),
    queryFn: async () => {
      // A day of photos fits in a few pages; the archive shows the whole day at once.
      const items = []
      let cursor: string | undefined
      do {
        const page = await call(api.GET('/api/media', { params: { query: { date, cursor, limit: 200 } } }))
        items.push(...page.items)
        cursor = page.nextCursor ?? undefined
      } while (cursor)
      return items
    },
  })

export const useMediaDays = (from: string, to: string) =>
  useQuery({ queryKey: keys.mediaDays(from, to), queryFn: () => call(api.GET('/api/media/days', { params: { query: { from, to } } })) })

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

export function useSetArmed() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (armed: boolean) => call(api.PUT('/api/modes/armed', { body: { armed } })),
    onSuccess: (data) => qc.setQueryData(['modes', 'armed'], data),
  })
}

export const useTelegram = () =>
  useQuery({ queryKey: ['telegram'], queryFn: () => call(api.GET('/api/me/telegram')) })
