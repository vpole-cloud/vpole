// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../shell/AppContext'
import type { AppServices } from '../shell/contracts'
import type { ResourceEndpointDiscovery } from '../types'
import { ResourceEndpoints } from './ResourceEndpoints'

let root: Root; let container: HTMLDivElement
const data: ResourceEndpointDiscovery = { resourceType: 'example/queue', resourceId: 'queue', displayName: 'Orders', availability: 'Supported',
  endpoints: [{ id: 'consumer', displayName: 'Queue endpoint', protocol: 'amqp', transport: 'TCP', port: 5672, configuration: [{ key: 'virtualHost', displayName: 'Virtual host', value: '/app' }] }], topology: { status: 'Eligible', reasonCode: null } }
const fetchMock = vi.fn()
beforeEach(() => { Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true }); container = document.createElement('div'); document.body.append(container); root = createRoot(container); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock) })
afterEach(async () => { await act(async () => root.unmount()); container.remove(); vi.unstubAllGlobals() })
async function render(tenantId = 'alpha') { await act(async () => root.render(<AppContext.Provider value={{ api: { baseUrl: 'http://localhost', token: 'bearer', tenantId } } as AppServices}><ResourceEndpoints resourceType="example/queue" resourceId="queue" source={{ resourceType: 'example/app', resourceId: 'source' }} /></AppContext.Provider>)) }
const json = (value: unknown) => new Response(JSON.stringify(value))
it('renders custom provider metadata and sends both scoped resource identities', async () => {
  fetchMock.mockResolvedValue(json(data)); await render()
  expect(container.textContent).toContain('Queue endpoint'); expect(container.textContent).toContain('amqp · TCP'); expect(container.textContent).toContain('Virtual host: /app'); expect(container.textContent).toContain('Deployment targets match')
  const [url, init] = fetchMock.mock.calls[0]; const query = new URL(url).searchParams
  expect(query.get('resourceType')).toBe('example/queue'); expect(query.get('sourceResourceType')).toBe('example/app'); expect(query.get('sourceResourceId')).toBe('source')
  expect(init.headers).toMatchObject({ Authorization: 'Bearer bearer', 'X-Pole-Tenant-ID': 'alpha' })
})
it.each(['empty', 'unsupported', 'unavailable', 'ineligible'])('distinguishes %s discovery from other states', async (state) => {
  fetchMock.mockResolvedValue(state === 'unavailable' ? new Response('Unavailable', { status: 503 }) : json({ ...data, endpoints: state === 'ineligible' ? data.endpoints : [], availability: state === 'unsupported' ? 'Unsupported' : 'Supported', topology: state === 'ineligible' ? { status: 'Ineligible', reasonCode: 'DestinationTargetMissing' } : { status: 'NotEvaluated', reasonCode: null } }))
  await render()
  expect(container.textContent).toContain({ empty: 'publishes no connection endpoints', unsupported: 'does not support connection endpoints', unavailable: 'Endpoint discovery is unavailable', ineligible: 'missing on at least one source deployment target' }[state]!)
  if (state === 'unavailable') expect(container.textContent).not.toContain('publishes no connection endpoints')
})
it('retains same-scope endpoints on refresh failure but discards them on tenant change', async () => {
  fetchMock.mockResolvedValueOnce(json(data)); await render(); fetchMock.mockResolvedValue(new Response('', { status: 503 }))
  await act(async () => container.querySelector('button')!.click())
  expect(container.textContent).toContain('Last fetched endpoints'); expect(container.textContent).toContain('Queue endpoint')
  await render('beta'); expect(container.textContent).not.toContain('Queue endpoint')
})
