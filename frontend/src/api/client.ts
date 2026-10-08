import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'

// Generated from the backend OpenAPI document: `npm run gen:api`. Do not hand-write request types.
export type Schemas = components['schemas']
export type EventDto = Schemas['EventDto']
export type EventPhotoDto = Schemas['EventPhotoDto']
export type MediaDto = Schemas['MediaDto']
export type MediaDayDto = Schemas['MediaDayDto']
export type DeviceDto = Schemas['DeviceDto']
export type LatestValueDto = Schemas['LatestValueDto']
export type MeDto = Schemas['MeDto']
export type LoginResult = Schemas['LoginResult']

/** Fired when any call returns 401: the session expired, the app shows the login screen. */
export const UNAUTHORIZED_EVENT = 'hive:unauthorized'

const unauthorized: Middleware = {
  onResponse({ response, request }) {
    if (response.status === 401 && !new URL(request.url).pathname.startsWith('/api/auth/')) {
      window.dispatchEvent(new Event(UNAUTHORIZED_EVENT))
    }
    return response
  },
}

export const api = createClient<paths>({ baseUrl: window.location.origin, credentials: 'same-origin' })
api.use(unauthorized)

export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message?: string) {
    super(message ?? `HTTP ${status}`)
    this.status = status
  }
}

/** Unwraps an openapi-fetch result for TanStack Query: data or a thrown ApiError. */
export async function call<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise
  if (!response.ok) {
    const detail = (error as { detail?: string } | undefined)?.detail
    throw new ApiError(response.status, detail)
  }
  return data as T
}
