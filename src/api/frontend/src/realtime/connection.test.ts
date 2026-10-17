import { beforeEach, expect, it, vi } from 'vitest'
import { createRealtimeConnection } from './connection'

const { hub, withUrl } = vi.hoisted(() => ({
  hub: {
    start: vi.fn(),
    stop: vi.fn(),
    invoke: vi.fn(),
    on: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
  },
  withUrl: vi.fn(),
}))
vi.mock('@microsoft/signalr', () => ({
  LogLevel: { None: 6 },
  HubConnectionBuilder: class {
    withUrl(url: string, options: unknown) {
      withUrl(url, options)
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    configureLogging() {
      return this
    }
    build() {
      return hub
    }
  },
}))

beforeEach(() => vi.resetAllMocks())

it('uses cookie-only hub credentials and normalizes nullable Subscribe and Heartbeat scopes', async () => {
  const wire = [
    {
      subscriptionId: 'stable-id',
      topic: 'operations',
      tenantId: null,
      resourceId: null,
      covered: true,
    },
  ]
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
  const handler = hub.on.mock.calls.find(([name]) => name === 'AccessRevoked')![1] as (
    tenantId: string | null,
  ) => void
  handler(null)
  expect(revoked).toHaveBeenCalledWith(undefined)
})

it.each([true, false, 'true', undefined, null, 1])(
  'accepts only the exact diagnostic boolean %s with cookie-only credentials and two bounded counts',
  async (accepted) => {
    hub.invoke.mockResolvedValue(accepted)
    const connection = createRealtimeConnection('api')
    const input = {
      reconnects: 200.5,
      fallbacks: -1,
      userId: 'must-not-be-sent',
      token: 'must-not-be-sent',
    }
    expect(await connection.reportLifecycle!(input)).toBe(accepted === true)
    expect(withUrl).toHaveBeenCalledWith('api/api/v1/realtime/hub', { withCredentials: true })
    expect(hub.invoke).toHaveBeenCalledWith('ReportClientLifecycle', {
      reconnects: 100,
      fallbacks: 0,
    })
  },
)

it('does not send empty or invalid diagnostic counts and swallows older-server and transport rejection', async () => {
  const connection = createRealtimeConnection('api')
  expect(await connection.reportLifecycle!({ reconnects: NaN, fallbacks: Infinity })).toBe(false)
  expect(await connection.reportLifecycle!({ reconnects: 0, fallbacks: 0 })).toBe(false)
  expect(hub.invoke).not.toHaveBeenCalled()
  hub.invoke.mockRejectedValue(new Error('Unknown diagnostic method'))
  expect(await connection.reportLifecycle!({ reconnects: 1, fallbacks: 2 })).toBe(false)
  hub.invoke.mockImplementation(() => {
    throw new Error('Diagnostic transport unavailable')
  })
  expect(await connection.reportLifecycle!({ reconnects: 1, fallbacks: 2 })).toBe(false)
  expect(hub.on).not.toHaveBeenCalled()
})
