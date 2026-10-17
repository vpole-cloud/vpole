import { buildHeaders, fetchJson, sendWithoutResponse } from '../api/transport'
export interface RealtimeSession {
  expiresAtUtc: string
}
export const createRealtimeSession = (baseUrl: string, token: string, signal: AbortSignal) =>
  fetchJson<RealtimeSession>(`${baseUrl}/api/v1/realtime/session`, {
    method: 'POST',
    credentials: 'include',
    headers: buildHeaders(token),
    signal,
  })
export const revokeRealtimeSession = (baseUrl: string) =>
  sendWithoutResponse(`${baseUrl}/api/v1/realtime/session`, {
    method: 'DELETE',
    credentials: 'include',
    headers: buildHeaders(),
    signal: AbortSignal.timeout(5000),
  })
