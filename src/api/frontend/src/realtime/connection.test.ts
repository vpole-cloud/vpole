import { expect, it, vi } from 'vitest'
import { createRealtimeConnection } from './connection'

const { hub, withUrl } = vi.hoisted(() => ({
  hub: { start: vi.fn(), stop: vi.fn(), invoke: vi.fn(), on: vi.fn(), onreconnecting: vi.fn(), onreconnected: vi.fn(), onclose: vi.fn() },
  withUrl: vi.fn(),
}))
vi.mock('@microsoft/signalr', () => ({
  LogLevel: { None: 6 },
  HubConnectionBuilder: class {
    withUrl(url: string, options: unknown) { withUrl(url, options); return this }
    withAutomaticReconnect() { return this }
    configureLogging() { return this }
    build() { return hub }
  },
}))

it('uses cookie-only hub credentials and normalizes nullable Subscribe and Heartbeat scopes', async () => {
  const wire = [{ subscriptionId: 'stable-id', topic: 'operations', tenantId: null, resourceId: null, covered: true }]
  hub.invoke.mockResolvedValue(wire)
  const connection = createRealtimeConnection('api')
  expect(withUrl).toHaveBeenCalledWith('api/api/v1/realtime/hub', { withCredentials: true })
  const expected = [{ ...wire[0], tenantId: undefined, resourceId: undefined }]
  expect(await connection.subscribe([{ topic: 'operations' }])).toEqual(expected)
  expect(hub.invoke).toHaveBeenLastCalledWith('Subscribe', [{ topic: 'operations' }])
  expect(await connection.heartbeat()).toEqual(expected)
  expect(hub.invoke).toHaveBeenLastCalledWith('Heartbeat')
  const revoked = vi.fn()
  connection.onAccessRevoked(revoked)
  const handler = hub.on.mock.calls.find(([name]) => name === 'AccessRevoked')![1] as (tenantId: string | null) => void
  handler(null)
  expect(revoked).toHaveBeenCalledWith(undefined)
})
