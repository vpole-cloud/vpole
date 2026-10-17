// @vitest-environment jsdom
import { useQuerySession } from '../data/querySession'
import type { QueryClient } from '@tanstack/react-query'
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../shell/AppShell'
import { createAppRegistry } from '../shell/moduleRegistry'
import type {
  ResourceDeploymentDiagnostics,
  ResourceDeploymentOperation,
  RuntimeLogFrame,
} from '../types'
import { ResourceRuntimeDiagnostics } from './ResourceRuntimeDiagnostics'

let operation: ResourceDeploymentOperation
let data: ResourceDeploymentDiagnostics
let active: boolean
let failure: string
let deferred: Promise<Response> | null
let controllers: ReadableStreamDefaultController<Uint8Array>[]
let canceled: boolean[]
let cacheClient: QueryClient
let root: Root
let container: HTMLDivElement
const encoder = new TextEncoder()
const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
function View() {
  const client = useQuerySession().client
  useLayoutEffect(() => {
    cacheClient = client
  }, [client])
  return <ResourceRuntimeDiagnostics operation={operation} active={active} />
}
const registry = createAppRegistry([
  {
    id: 'diagnostics',
    views: [{ id: 'diagnostics', label: 'Diagnostics', access: 'tenant', component: View }],
  },
])
function stream() {
  const index = controllers.length
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controllers.push(controller)
      canceled.push(false)
      controller.enqueue(
        encoder.encode(
          `${JSON.stringify({ kind: 'Opened', text: null, reason: null, outputBytes: 0 })}\n`,
        ),
      )
    },
    cancel() {
      canceled[index] = true
    },
  })
  return new Response(body, { headers: { 'Content-Type': 'application/x-ndjson' } })
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear()
  window.history.replaceState(null, '', '/?view=diagnostics')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  operation = {
    deployment: {
      id: 'placement',
      resourceType: 'pole/third-party',
      resourceId: 'resource',
      resourceGroupId: 'group',
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      placement: {
        deploymentId: 'placement',
        deploymentTargetId: 'target',
        isolationScopeId: 'scope',
        runtimeCellId: 'cell',
        desiredGeneration: 2,
        observedGeneration: 2,
        deploymentRevision: 7,
        resourceRevision: 2,
        fixedReplicas: 1,
        desiredOperation: 'Apply',
        status: 'Applied',
        endpoints: [],
        compute: {
          displayName: 'Small',
          profile: { computeProfileId: 'small', computeProfileRevision: 1 },
          requests: { cpuMillis: 100, memoryMi: 128 },
          limits: { cpuMillis: 200, memoryMi: 256 },
          extendedResources: [],
        },
      },
    },
    resourceDisplayName: 'Application',
    targetDisplayName: 'Local cluster',
    phase: 'Applied',
    attemptCount: 0,
    sinceUtc: new Date().toISOString(),
    retryable: false,
    retryNotBeforeUtc: null,
  }
  data = {
    deploymentId: 'placement',
    resourceType: 'pole/third-party',
    resourceId: 'resource',
    targetId: 'target',
    deploymentRevision: 7,
    desiredGeneration: 2,
    checkedAtUtc: new Date().toISOString(),
    diagnostics: {
      kind: 'Available',
      instancesTruncated: true,
      eventsTruncated: true,
      eventsUnavailableReason: null,
      instances: [
        {
          uid: 'owned-uid',
          name: 'application-0',
          phase: 'Running',
          ready: false,
          currentGeneration: false,
          restartCount: 3,
          reasons: ['CrashLoopBackOff'],
          containers: [
            { name: 'app', kind: 'Application', previousAvailable: true },
            { name: 'sidecar', kind: 'Application', previousAvailable: false },
          ],
          containersTruncated: false,
        },
      ],
      events: [{ objectKind: 'Pod', objectName: 'application-0', reason: 'Unhealthy', count: 4 }],
    },
  }
  active = true
  failure = ''
  deferred = null
  controllers = []
  canceled = []
  fetchMock
    .mockReset()
    .mockImplementation((input) =>
      String(input).includes('/runtime-logs?')
        ? (deferred ??
          Promise.resolve(
            failure
              ? new Response(
                  JSON.stringify({ reason: failure, message: 'raw-private-error-canary' }),
                  { status: 409 },
                )
              : stream(),
          ))
        : String(input).endsWith('/diagnostics')
          ? Promise.resolve(new Response(JSON.stringify(data)))
          : Promise.resolve(new Response('[]')),
    )
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  vi.unstubAllGlobals()
})
async function render(tenantId = 'active', token = 'token') {
  const tenant = { id: tenantId, name: tenantId, role: 'Viewer' as const, isDefault: true }
  await act(async () =>
    root.render(
      <AppShell
        registry={registry}
        initialViewId="diagnostics"
        api={{ baseUrl: 'http://localhost', token }}
        auth={{
          initialized: true,
          authenticated: true,
          userId: 'user',
          isPlatformAdmin: false,
          login: () => {},
          logout: () => {},
        }}
        tenants={{
          items: [tenant],
          activeTenant: tenant,
          activeTenantId: tenantId,
          selectTenant: () => {},
          status: 'success',
          error: '',
        }}
      />,
    ),
  )
}
const text = () => container.querySelector('#app-content')!.textContent!
const reads = () => fetchMock.mock.calls.filter(([url]) => String(url).includes('/runtime-logs?'))
async function click(label: string) {
  await act(async () =>
    [...container.querySelectorAll('button')]
      .find((button) => button.textContent === label)!
      .click(),
  )
}
async function select(label: string, value: string) {
  await act(async () => {
    const control = [...container.querySelectorAll('label')]
      .find((item) => item.textContent?.startsWith(label))!
      .querySelector('select')!
    control.value = value
    control.dispatchEvent(new Event('change', { bubbles: true }))
  })
}
async function open() {
  await render()
  await click('Runtime logs and events')
  await select('Instance', 'owned-uid')
  await select('Container', 'app')
}
async function emit(index: number, frame: RuntimeLogFrame) {
  await act(async () => controllers[index].enqueue(encoder.encode(`${JSON.stringify(frame)}\n`)))
}

it('reads the exact instance/container with bearer and tenant headers and renders output as text', async () => {
  await open()
  expect(text()).toContain('earlier generation')
  expect(text()).toContain('Not ready')
  expect(text()).toContain('Unhealthy')
  expect(text()).toContain('event sample is limited')
  await click('Follow runtime logs')
  const [url, init] = reads()[0]
  const query = new URL(String(url)).searchParams
  expect(query.get('instanceUid')).toBe('owned-uid')
  expect(query.get('container')).toBe('app')
  expect(query.get('expectedRevision')).toBe('7')
  expect(query.get('follow')).toBe('true')
  expect(query.has('namespace')).toBe(false)
  expect(query.has('targetId')).toBe(false)
  expect(init?.headers).toEqual(
    expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'active' }),
  )
  await emit(0, {
    kind: 'Output',
    text: '<script>runtime-canary()</script>\n',
    reason: null,
    outputBytes: 40,
  })
  expect(container.querySelector('pre')?.textContent).toBe('<script>runtime-canary()</script>\n')
  expect(container.querySelector('pre script')).toBeNull()
  expect(JSON.stringify(localStorage)).not.toContain('runtime-canary')
  expect(JSON.stringify(sessionStorage)).not.toContain('runtime-canary')
  await select('Container', 'sidecar')
  expect(canceled[0]).toBe(true)
  expect(reads()[0][1]?.signal?.aborted).toBe(true)
  expect(text()).not.toContain('runtime-canary')
})

it('closes and hides streams, clears output and discards late successful headers after tenant changes', async () => {
  await open()
  await click('Follow runtime logs')
  await emit(0, { kind: 'Output', text: 'old-tenant-canary', reason: null, outputBytes: 18 })
  await click('Close runtime diagnostics')
  expect(canceled[0]).toBe(true)
  expect(text()).not.toContain('old-tenant-canary')
  await click('Runtime logs and events')
  await select('Instance', 'owned-uid')
  await select('Container', 'app')
  let complete!: (response: Response) => void
  deferred = new Promise((resolve) => {
    complete = resolve
  })
  await click('Follow runtime logs')
  const old = reads().at(-1)!
  await render('new')
  expect(old[1]?.signal?.aborted).toBe(true)
  await act(async () => complete(stream()))
  expect(canceled[1]).toBe(true)
  expect(text()).not.toContain('Runtime diagnostics · Local cluster')
  deferred = null
  await click('Runtime logs and events')
  await select('Instance', 'owned-uid')
  await select('Container', 'app')
  await click('Follow runtime logs')
  active = false
  await render('new')
  expect(canceled[2]).toBe(true)
  expect(text()).not.toContain('Runtime diagnostics · Local cluster')
})

it('explains replacement and reconnects with a fresh window rather than a retained cursor', async () => {
  await open()
  await click('Follow runtime logs')
  await emit(0, { kind: 'Output', text: 'earlier-window-canary', reason: null, outputBytes: 21 })
  await emit(0, { kind: 'Ended', text: null, reason: 'InstanceReplaced', outputBytes: 21 })
  expect(text()).toContain('explicitly select its replacement')
  expect(canceled[0]).toBe(true)
  await click('Refresh instances and events')
  expect(text()).not.toContain('earlier-window-canary')
  await select('Instance', 'owned-uid')
  await select('Container', 'app')
  await click('Read recent logs')
  await emit(1, { kind: 'Output', text: 'new-window-marker', reason: null, outputBytes: 17 })
  await emit(1, { kind: 'Ended', text: null, reason: 'EndOfWindow', outputBytes: 17 })
  expect(text()).toContain('new-window-marker')
  expect(text()).not.toContain('earlier-window-canary')
  expect(new URL(String(reads()[1][0])).searchParams.has('afterSequence')).toBe(false)
})

it('shows safe target outage and rejects mismatched discovery before opening logs', async () => {
  await open()
  failure = 'TargetUnavailable'
  await click('Read recent logs')
  expect(text()).toContain('Other targets can be inspected independently')
  expect(text()).not.toContain('raw-private-error-canary')
  failure = ''
  data = { ...data, resourceId: 'foreign-resource' }
  await click('Refresh instances and events')
  expect(text()).toContain('Refresh operations before opening')
  expect(reads()).toHaveLength(1)
  data = { ...data, resourceId: 'resource', desiredGeneration: 3 }
  await click('Refresh instances and events')
  expect(text()).toContain('Refresh operations before opening')
  expect(reads()).toHaveLength(1)
})

it('bounds the displayed tail and reports server lifetime/output limits independently', async () => {
  await open()
  await click('Follow runtime logs')
  let bytes = 0
  for (let i = 0; i < 10; i++) {
    bytes += 8000
    await emit(0, {
      kind: 'Output',
      text: i === 0 ? `early-marker${'x'.repeat(7988)}` : 'later-marker'.repeat(666),
      reason: null,
      outputBytes: bytes,
    })
  }
  expect(container.querySelector('pre')!.textContent!.length).toBeLessThanOrEqual(65536)
  expect(text()).not.toContain('early-marker')
  expect(text()).toContain('Earlier output left this bounded view')
  await emit(0, { kind: 'Ended', text: null, reason: 'OutputLimit', outputBytes: bytes })
  expect(text()).toContain('server output limit')
})

it('handles unsupported providers and malformed or incomplete streams without interpreting data', async () => {
  data = { ...data, diagnostics: { kind: 'Unavailable', reason: 'Unsupported' } }
  await render()
  await click('Runtime logs and events')
  expect(text()).toContain('does not support runtime diagnostics')
  expect(reads()).toHaveLength(0)
  data = {
    ...data,
    diagnostics: {
      kind: 'Available',
      instances: [
        {
          uid: 'owned-uid',
          name: 'application-0',
          phase: 'Running',
          ready: true,
          currentGeneration: true,
          restartCount: 0,
          reasons: [],
          containers: [{ name: 'app', kind: 'Application', previousAvailable: false }],
          containersTruncated: false,
        },
      ],
      instancesTruncated: false,
      events: [],
      eventsTruncated: false,
    },
  }
  await click('Refresh instances and events')
  await select('Instance', 'owned-uid')
  await select('Container', 'app')
  await click('Read recent logs')
  await act(async () =>
    controllers[0].enqueue(
      encoder.encode('{"kind":"<script>malformed-private-canary</script>"}\n'),
    ),
  )
  expect(text()).toContain('response exceeded the read limit')
  expect(text()).not.toContain('malformed-private-canary')
  expect(canceled[0]).toBe(true)
  await click('Reconnect recent logs')
  await act(async () => controllers[1].close())
  expect(text()).toContain('without a completion marker')
  await click('Reconnect recent logs')
  await act(async () => controllers[2].error(new Error('private-transport-canary')))
  expect(text()).toContain('connection was interrupted')
  expect(text()).not.toContain('private-transport-canary')
})

it('keeps an authorized stream across token renewal and uses the new token for explicit reconnect', async () => {
  await open()
  await click('Follow runtime logs')
  await emit(0, { kind: 'Output', text: 'ephemeral-runtime-canary', reason: null, outputBytes: 24 })
  await render('active', 'renewed-token')
  expect(canceled[0]).toBe(false)
  expect(container.querySelector('pre')?.textContent).toContain('ephemeral-runtime-canary')
  expect(
    JSON.stringify(
      cacheClient
        .getQueryCache()
        .getAll()
        .map((query) => query.state.data),
    ),
  ).not.toContain('ephemeral-runtime-canary')
  expect(cacheClient.getMutationCache().getAll()).toHaveLength(0)
  await click('Stop reading')
  await click('Reconnect recent logs')
  expect(reads().at(-1)![1]?.headers).toEqual(
    expect.objectContaining({ Authorization: 'Bearer renewed-token' }),
  )
})

it('clears the active stream immediately when the deployment revision is replaced', async () => {
  await open()
  await click('Follow runtime logs')
  await emit(0, { kind: 'Output', text: 'superseded-output-canary', reason: null, outputBytes: 24 })
  operation = {
    ...operation,
    deployment: {
      ...operation.deployment,
      placement: { ...operation.deployment.placement, deploymentRevision: 8, desiredGeneration: 3 },
    },
  }
  await render()
  expect(canceled[0]).toBe(true)
  expect(text()).not.toContain('superseded-output-canary')
  expect(text()).not.toContain('Runtime diagnostics · Local cluster')
})
