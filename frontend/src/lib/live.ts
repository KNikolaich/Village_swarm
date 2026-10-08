import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import type { DeviceDto, EventDto, LatestValueDto } from '@/api/client'
import { keys, patchEventInCache, type EventFilters } from '@/api/queries'
import { eventTypeLabel } from '@/i18n'

// Messages pushed by the api on /hubs/live (backend/src/Hive.Api/Live/LiveHub.cs).
type LiveTelemetry = { deviceId: string; metric: string; ts: string; value: number }
type LiveDeviceStatus = { deviceId: string; status: string; at: string }
type LiveDeviceHealth = { deviceId: string; ts: string; rssi: number; uptimeS: number; heapFree: number; vbat?: number | null }

export type LiveState = 'connecting' | 'connected' | 'disconnected'

type EventsPage = { items: EventDto[]; nextCursor?: string | null }

function matches(filters: EventFilters, e: EventDto) {
  return (!filters.type || filters.type === e.type) && (!filters.device || filters.device === e.deviceId)
}

/** Keeps a SignalR connection while logged in and applies live updates to the query cache. */
export function useLive(enabled: boolean): LiveState {
  const qc = useQueryClient()
  const [state, setState] = useState<LiveState>('connecting')

  useEffect(() => {
    if (!enabled) return
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/live')
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30_000, 1000 * 2 ** ctx.previousRetryCount) })
      // The header dot shows connection trouble; StrictMode remounts in dev would only add console noise.
      .configureLogging(LogLevel.Critical)
      .build()

    connection.on('Event', (e: EventDto) => {
      // Newest first: prepend to the first page of every matching list (filters are part of the query key).
      for (const [key, data] of qc.getQueriesData<InfiniteData<EventsPage>>({ queryKey: keys.eventsAll })) {
        const filters = (key[1] ?? {}) as EventFilters
        if (!data || !matches(filters, e) || data.pages[0]?.items.some((x) => x.id === e.id)) continue
        qc.setQueryData<InfiniteData<EventsPage>>(key, {
          ...data,
          pages: [{ ...data.pages[0], items: [e, ...data.pages[0].items] }, ...data.pages.slice(1)],
        })
      }
      if (e.severity === 'alarm' || e.severity === 'critical') {
        toast.error(`${eventTypeLabel(e.type)}: ${e.zone ?? e.deviceName ?? e.deviceId}`, { duration: 10_000 })
      }
      // Photos are uploaded right after the event; refresh it once they had time to arrive.
      const expected = (e.payload as { photos?: unknown[] } | null)?.photos?.length ?? 0
      if (expected > e.photos.length) {
        setTimeout(async () => {
          const res = await fetch(`/api/events/${e.id}`, { credentials: 'same-origin' })
          if (res.ok) patchEventInCache(qc, (await res.json()) as EventDto)
        }, 4000)
      }
    })

    connection.on('Telemetry', (t: LiveTelemetry) => {
      qc.setQueryData<LatestValueDto[]>(keys.latest, (list) => {
        if (!list) return list
        const rest = list.filter((x) => !(x.deviceId === t.deviceId && x.metric === t.metric))
        return [...rest, { deviceId: t.deviceId, metric: t.metric, ts: t.ts, value: t.value }]
      })
    })

    connection.on('DeviceStatus', (s: LiveDeviceStatus) => {
      qc.setQueryData<DeviceDto[]>(keys.devices, (list) =>
        list?.map((d) => (d.deviceId === s.deviceId ? { ...d, status: s.status, lastSeenAt: s.at } : d)),
      )
      if (!qc.getQueryData<DeviceDto[]>(keys.devices)?.some((d) => d.deviceId === s.deviceId)) {
        void qc.invalidateQueries({ queryKey: keys.devices }) // a new hornet
      }
    })

    connection.on('DeviceHealth', (h: LiveDeviceHealth) => {
      qc.setQueryData<DeviceDto[]>(keys.devices, (list) =>
        list?.map((d) => (d.deviceId === h.deviceId ? { ...d, rssi: h.rssi, status: 'online', lastSeenAt: h.ts } : d)),
      )
    })

    connection.onreconnecting(() => setState('connecting'))
    connection.onreconnected(() => {
      setState('connected')
      void qc.invalidateQueries() // catch up on what was missed while disconnected
    })
    connection.onclose(() => setState('disconnected'))

    let stopped = false
    const start = async () => {
      try {
        await connection.start()
        if (!stopped) setState('connected')
      } catch {
        if (!stopped) {
          setState('disconnected')
          setTimeout(() => !stopped && connection.state === HubConnectionState.Disconnected && void start(), 5000)
        }
      }
    }
    void start()

    return () => {
      stopped = true
      void connection.stop()
    }
  }, [enabled, qc])

  return enabled ? state : 'disconnected'
}
