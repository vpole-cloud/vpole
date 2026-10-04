// @vitest-environment jsdom
import { act, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../shell/AppShell'
import { createAppRegistry } from '../shell/moduleRegistry'
import type { SourceBuildLogs } from '../types'
import { SourceBuildLogViewer } from './SourceBuildLogViewer'

let jobId: string
let pages: SourceBuildLogs[]
let root: Root
let container: HTMLDivElement
let readFailure: boolean
let slowRead: Promise<Response> | null
const json = (data: unknown) => Promise.resolve(new Response(JSON.stringify(data)))
const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
function View() {
  const [closed, setClosed] = useState(false)
  return closed ? <p>Log view closed.</p> : <SourceBuildLogViewer jobId={jobId} webServiceId="service" onClose={() => setClosed(true)} />
}
const registry = createAppRegistry([{ id: 'logs', views: [{ id: 'logs', label: 'Logs', access: 'tenant', component: View }] }])
function page(sequence: number, text: string, options: Partial<SourceBuildLogs> = {}): SourceBuildLogs {
  return { buildJobId: jobId, webServiceId: 'service', captureState: 'Open', entries: [{ sequence, text, stream: 'stderr', recordedAtUtc: new Date().toISOString() }], nextSequence: sequence, lastSequence: sequence, hasMore: false, truncated: false,
    expiresAtUtc: new Date(Date.now() + 86400000).toISOString(), capturedBytes: text.length, maximumBytes: 1024 * 1024, ...options }
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear(); window.history.replaceState(null, '', '/?view=logs')
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  jobId = 'aaaaaaaaaaaaaaaaaaaaaaaa'; pages = [page(1, 'first diagnostic\n')]; readFailure = false; slowRead = null
  fetchMock.mockReset().mockImplementation((input) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/logs')) {
      if (slowRead) return slowRead
      if (readFailure) return Promise.resolve(new Response('raw-private-problem-canary', { status: 503 }))
      const after = Number(url.searchParams.get('afterSequence'))
      return json(pages.find(value => value.nextSequence === after + 1) ?? pages.find(value => value.entries.length === 0 && value.nextSequence === after) ?? page(after, '', { entries: [], nextSequence: after, lastSequence: after }))
    }
    if (url.pathname.includes('/source-build-jobs/')) return json({ id: jobId, webServiceId: 'service', branch: 'main', commitSha: 'abcdef1234', state: { status: 'building' } })
    return json([])
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); vi.useRealTimers(); vi.unstubAllGlobals() })
async function render(tenantId = 'active') {
  const tenant = { id: tenantId, name: tenantId, role: 'Viewer' as const, isDefault: true }
  await act(async () => root.render(<AppShell registry={registry} initialViewId="logs" api={{ baseUrl: 'http://localhost', token: 'token' }}
    auth={{ initialized: true, authenticated: true, userId: 'user', isPlatformAdmin: false, login: () => {}, logout: () => {} }}
    tenants={{ items: [tenant], activeTenant: tenant, activeTenantId: tenantId, selectTenant: () => {}, status: 'success', error: '' }} />))
}
async function click(label: string) { await act(async () => [...container.querySelectorAll('button')].find(button => button.textContent === label)!.click()) }
const reads = () => fetchMock.mock.calls.filter(([url]) => String(url).includes('/logs?'))
const text = () => container.querySelector('#app-content')!.textContent!

it('follows exact cursors without duplicate ranges, pauses and resumes, and renders untrusted text', async () => {
  vi.useFakeTimers(); pages = [page(1, '<script>untrusted()</script>\n'), page(2, 'next diagnostic\n')]
  await render(); expect(container.querySelector('pre')?.textContent).toContain('<script>untrusted()</script>'); expect(container.querySelector('pre script')).toBeNull()
  await click('Pause follow'); const count = reads().length
  await act(async () => { await vi.advanceTimersByTimeAsync(6000) }); expect(reads()).toHaveLength(count)
  await click('Follow logs'); await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
  expect(new URL(String(reads().at(-1)![0])).searchParams.get('afterSequence')).toBe('1')
  await click('Refresh logs'); expect(container.querySelector('pre')?.textContent?.match(/next diagnostic/g)).toHaveLength(1)
  for (const [, init] of fetchMock.mock.calls) expect(init?.headers).toEqual(expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'active' }))
  expect(fetchMock.mock.calls.some(([, init]) => init?.method && init.method !== 'GET')).toBe(false)
})

it('preserves diagnostic words and lines split across captured ranges', async () => {
  pages = [page(1, 'failure: missing-fi', { hasMore: true, lastSequence: 2 }), page(2, 'le.txt not found\n', { captureState: 'Completed' })]
  await render()
  expect(container.querySelector('pre')?.textContent).toBe('failure: missing-file.txt not found\n')
  expect(text()).toContain('Capture Completed')
})

it('retains output and cursor on outage and reconnects without reflecting server errors', async () => {
  await render(); readFailure = true; await click('Refresh logs')
  expect(text()).toContain('first diagnostic'); expect(text()).toContain('Previously displayed output is retained'); expect(text()).not.toContain('raw-private-problem-canary')
  pages.push(page(2, 'recovered diagnostic\n', { captureState: 'Completed' })); readFailure = false
  await click('Reconnect logs'); expect(new URL(String(reads().at(-1)![0])).searchParams.get('afterSequence')).toBe('1'); expect(text()).toContain('Capture Completed'); expect(text()).toContain('recovered diagnostic')
})

it('aborts closing reads without a build write and ignores their late success', async () => {
  let complete!: (response: Response) => void
  slowRead = new Promise(resolve => { complete = resolve }); await render()
  const old = reads()[0]
  await click('Close logs'); expect(old[1]?.signal?.aborted).toBe(true)
  await act(async () => complete(new Response(JSON.stringify(page(1, 'late-closed-canary')))))
  expect(text()).toContain('Log view closed'); expect(text()).not.toContain('late-closed-canary'); expect(fetchMock.mock.calls.every(([, init]) => !init?.method || init.method === 'GET')).toBe(true)
})

it('clears tenant and job buffers and ignores stale successful responses', async () => {
  await render(); expect(text()).toContain('first diagnostic')
  let complete!: (response: Response) => void
  slowRead = new Promise(resolve => { complete = resolve }); await act(async () => [...container.querySelectorAll('button')].find(button => button.textContent === 'Refresh logs')!.click())
  const old = reads().at(-1)!
  slowRead = null; pages = [page(1, 'new-tenant-diagnostic')]; await render('new')
  expect(old[1]?.signal?.aborted).toBe(true); expect(text()).not.toContain('first diagnostic'); expect(text()).toContain('new-tenant-diagnostic')
  await act(async () => complete(new Response(JSON.stringify(page(2, 'foreign-tenant-canary')))))
  expect(text()).not.toContain('foreign-tenant-canary')
  jobId = 'bbbbbbbbbbbbbbbbbbbbbbbb'; pages = [page(1, 'new-job-diagnostic')]; await render('new')
  expect(text()).not.toContain('new-tenant-diagnostic'); expect(text()).toContain('new-job-diagnostic')
})

it('explains truncation and interruption, bounds the displayed tail and purges it on expiry', async () => {
  pages = Array.from({ length: 40 }, (_, i) => page(i + 1, `${i === 0 ? 'early-canary' : 'retained-marker'}${'x'.repeat(8100)}\n`, { hasMore: i < 39, lastSequence: 40, captureState: 'Interrupted', truncated: true }))
  await render(); expect(text()).toContain('retained prefix'); expect(text()).toContain('worker stopped renewing'); expect(text()).toContain('viewer keeps a bounded tail')
  expect(container.querySelector('pre')?.textContent).not.toContain('early-canary')
  expect(new TextEncoder().encode(container.querySelector('pre')?.textContent ?? '').length).toBeLessThan(270000)
  pages = [page(40, '', { entries: [], captureState: 'Expired', lastSequence: 40 })]
  await click('Refresh logs'); expect(text()).toContain('retention period expired'); expect(container.querySelector('pre')).toBeNull()
})

it('rejects missing or repeated ranges without silently advancing the displayed cursor', async () => {
  await render(); pages = [page(2, 'gap-canary', { entries: [{ sequence: 3, text: 'gap-canary', stream: 'stdout', recordedAtUtc: new Date().toISOString() }] })]
  await click('Refresh logs'); expect(text()).toContain('retained log range changed'); expect(text()).not.toContain('gap-canary')
  await click('Reconnect logs'); expect(new URL(String(reads().at(-1)![0])).searchParams.get('afterSequence')).toBe('1')
})

it('keeps a slow log read alive across follow ticks and rejects a mismatched resource identity', async () => {
  vi.useFakeTimers()
  let complete!: (response: Response) => void
  slowRead = new Promise(resolve => { complete = resolve }); await render()
  const first = reads()[0]
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(reads()).toHaveLength(1); expect(first[1]?.signal?.aborted).toBe(false)
  await act(async () => complete(new Response(JSON.stringify(page(1, 'wrong-resource-canary', { webServiceId: 'foreign-service' })))))
  expect(text()).toContain('returned build identity changed'); expect(text()).not.toContain('wrong-resource-canary')
})

it('does not request or display output when an exact job belongs to another service', async () => {
  fetchMock.mockImplementation((input) => String(input).includes('/source-build-jobs/') ? json({ id: jobId, webServiceId: 'other-service', branch: 'foreign-branch-canary', state: { status: 'failed' } }) : json([]))
  await render(); expect(reads()).toHaveLength(0)
  expect(text()).toContain('unavailable for the selected service'); expect(text()).not.toContain('foreign-branch-canary')
})
