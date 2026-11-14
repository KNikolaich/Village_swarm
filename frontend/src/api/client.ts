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
export type MonitoringDto = Schemas['MonitoringDto']
export type TimelapseDto = Schemas['TimelapseDto']
export type WeatherDto = Schemas['WeatherDto']
export type ZoneDto = Schemas['ZoneDto']
export type RuleDto = Schemas['RuleDto']
export type RuleDefinition = Schemas['RuleDefinition']
export type RuleTrigger = Schemas['RuleTrigger']
export type RuleCondition = Schemas['RuleCondition']
export type RuleAction = Schemas['RuleAction']
export type DryRunResult = Schemas['DryRunResult']
export type RuleRunDto = Schemas['RuleRunDto']
export type BackupStatusDto = Schemas['BackupStatusDto']
export type BackupRunDto = Schemas['BackupRunDto']
export type RetentionStatusDto = Schemas['RetentionStatusDto']
export type MeDto = Schemas['MeDto']
export type LoginResult = Schemas['LoginResult']
export type NoteNodeDto = Schemas['NoteNodeDto']
export type NoteDto = Schemas['NoteDto']
export type NoteAttachmentDto = Schemas['NoteAttachmentDto']
export type NoteSearchHitDto = Schemas['NoteSearchHitDto']
export type TrashedNoteDto = Schemas['TrashedNoteDto']

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
  /** True when the message came from the server (a reason a person can read), not the fallback "HTTP 500". */
  readonly fromServer: boolean
  constructor(status: number, message?: string, fromServer = false) {
    super(message ?? `HTTP ${status}`)
    this.status = status
    this.fromServer = fromServer && !!message
  }
}

/** What to show in an error toast: the server's own reason when it gave one (e.g. why the observer node refuses), else the generic text. */
export function errorText(e: unknown, fallback: string): string {
  return e instanceof ApiError && e.fromServer ? e.message : fallback
}

/** The server's own words out of an error body (a problem document), if it said any. */
function problemText(error: unknown): string | undefined {
  const body = error as { detail?: string; title?: string; errors?: Record<string, string[]> } | undefined
  const validation = body?.errors ? Object.values(body.errors).flat().join(' ') : undefined
  // Validation problems carry a generic title ("One or more validation errors occurred"): the field messages say more.
  return validation || body?.detail || body?.title || undefined
}

/** For a raw openapi-fetch result that was not ok: the server's reason, else the fallback text. */
export function failureText(result: { error?: unknown }, fallback: string): string {
  return problemText(result.error) ?? fallback
}

/** Unwraps an openapi-fetch result for TanStack Query: data or a thrown ApiError. */
export async function call<T>(promise: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await promise
  if (!response.ok) throw new ApiError(response.status, problemText(error), true)
  return data as T
}
