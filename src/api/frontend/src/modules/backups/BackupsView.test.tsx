// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import type { AppServices } from '../../shell/contracts'
import { createTestApp } from '../../test/TestApp'
import { deferred, strictFetch } from '../../test/http'
import { BackupsView } from './BackupsView'

const requests = strictFetch()
let app: ReturnType<typeof createTestApp>
let unavailable = false
let retired = false
let policyRevision = 1
let nextScheduledAt: string | null = null
let submittedRevision: number | undefined
let nextCatalogRead: Promise<Response> | null = null
let restoreAttempts: Record<string, unknown>[] = []
let rejectNextRestore = false
let failArchiveRefresh = false
let failRefreshAfterRestore = false
let pagedHistory = false
const services = (tenantId: string, role: 'Owner' | 'Member' = 'Owner'): AppServices => ({
  api: { baseUrl: 'https://api.test', tenantId, token: 'current' },
  auth: {
    initialized: true,
    authenticated: true,
    userId: 'user',
    isPlatformAdmin: false,
    login: () => {},
    logout: () => {},
  },
  tenant: {
    active: { id: tenantId, name: tenantId, role, isDefault: false },
    canManage: true,
  },
  navigation: {
    viewId: 'backups',
    search: '',
    setSearch: () => {},
    goTo: () => {},
    openResource: () => {},
    resourceHref: () => '/',
    registerUnsavedChanges: () => () => {},
  },
  theme: { value: 'light', set: () => {} },
  notifications: { publish: () => {} },
  resourceTypes: [],
})

beforeEach(() => {
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: {
      configurable: true,
      value() {
        this.open = true
      },
    },
    close: {
      configurable: true,
      value() {
        this.open = false
      },
    },
  })
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  unavailable = false
  retired = false
  policyRevision = 1
  nextScheduledAt = null
  submittedRevision = undefined
  nextCatalogRead = null
  restoreAttempts = []
  rejectNextRestore = false
  failArchiveRefresh = false
  failRefreshAfterRestore = false
  pagedHistory = false
  app = createTestApp()
  requests.reset()
  vi.stubGlobal('fetch', requests.fetch)
  requests.fetch.mockImplementation((input, init) => {
    const path = new URL(String(input)).pathname
    if (path === '/api/v1/backups/policies/policy' && init?.method === 'PUT') {
      submittedRevision = JSON.parse(String(init.body)).expectedRevision
      return Promise.resolve(Response.json({ id: 'policy', revision: 3 }))
    }
    if (path === '/api/v1/backups/operations') {
      const older = new URL(String(input)).searchParams.get('after')
      return Promise.resolve(
        Response.json(
          pagedHistory
            ? {
                items: [
                  {
                    id: older ? 'old-operation' : 'new-operation',
                    archiveId: 'archive',
                    kind: 'Backup',
                    state: 'Succeeded',
                    createdAt: '2026-11-13T00:00:00Z',
                  },
                ],
                nextCursor: older ? null : 'opaque-older-page',
              }
            : { items: [], nextCursor: null },
        ),
      )
    }
    if (path === '/api/v1/backups/policies')
      return Promise.resolve(
        Response.json([
          {
            id: 'policy',
            archiveId: 'archive',
            name: 'nightly',
            revision: policyRevision,
            appliedRevision: policyRevision,
            nextScheduledAt,
            deleting: false,
            resourceType: 'pole/postgresql',
            resourceId: 'cluster',
            destinationId: 'destination',
            schedule: policyRevision === 1 ? '0 2 * * *' : '0 4 * * *',
            suspended: false,
            retentionDays: 30,
          },
        ]),
      )
    if (path === '/api/v1/secrets/')
      return Promise.resolve(Response.json({ items: [], nextCursor: null }))
    if (path === '/api/v1/postgres/')
      return Promise.resolve(Response.json({ items: [], totalCount: 0 }))
    if (path === '/api/v1/backups/archives/archive/restores' && init?.method === 'POST') {
      restoreAttempts.push(JSON.parse(String(init.body)))
      if (rejectNextRestore) {
        rejectNextRestore = false
        return Promise.reject(new TypeError('Connection interrupted'))
      }
      failArchiveRefresh = failRefreshAfterRestore
      return Promise.resolve(
        Response.json({ id: 'restore-operation', kind: 'Restore', state: 'Pending' }),
      )
    }
    if (path === '/api/v1/backups/archives' && failArchiveRefresh)
      return Promise.resolve(
        new Response('{"title":"Archive refresh unavailable"}', { status: 503 }),
      )
    if (path === '/api/v1/backups/archives')
      return Promise.resolve(
        Response.json([
          {
            id: 'archive',
            resourceName: 'orders',
            serverName: 'pg-orders',
            active: !retired,
            supportsPointInTimeRecovery: true,
          },
        ]),
      )
    if (path === '/api/v1/backups/archives/archive/backups' && nextCatalogRead) {
      const pending = nextCatalogRead
      nextCatalogRead = null
      return pending
    }
    if (path === '/api/v1/backups/archives/archive/backups')
      return Promise.resolve(
        unavailable
          ? new Response('{"title":"Catalog unavailable"}', { status: 503 })
          : Response.json([{ backupId: 'older', completedAt: '2026-11-12T02:00:00Z' }]),
      )
    if (
      [
        '/api/v1/backups/destinations',
        '/api/v1/backups/policies',
        '/api/v1/backups/operations',
      ].includes(path)
    )
      return Promise.resolve(
        Response.json(
          path.endsWith('/destinations')
            ? [{ id: 'destination', name: 'storage', revision: 1 }]
            : [],
        ),
      )
    return requests.unhandled(input, init)
  })
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
  requests.verify()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})
async function render(tenantId = 'alpha', role: 'Owner' | 'Member' = 'Owner') {
  await act(async () =>
    app.root.render(
      <AppContext.Provider value={services(tenantId, role)}>
        <BackupsView />
      </AppContext.Provider>,
    ),
  )
}
async function selectArchive() {
  await act(async () => {
    const select = app.container.querySelector('select')!
    select.value = 'archive'
    select.dispatchEvent(new Event('change', { bubbles: true }))
  })
}

it('keeps the draft revision when refreshed metadata reopens the same policy', async () => {
  await render()
  const click = async (label: string) =>
    act(async () => {
      ;[...app.container.querySelectorAll('button')]
        .find((item) => item.textContent === label)!
        .click()
    })
  await click('Edit')
  await act(async () => {
    const field = app.container.querySelector('input[value="0 2 * * *"]')!
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(
      field,
      '0 3 * * *',
    )
    field.dispatchEvent(new Event('input', { bubbles: true }))
  })
  policyRevision = 2
  await click('Refresh')
  await click('Edit')
  await act(async () => {
    app.container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
  expect(submittedRevision).toBeUndefined()
  expect(app.container.textContent).toContain('This policy changed on the server')
  await click('Reapply my changes')
  await act(async () => {
    app.container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
  expect(submittedRevision).toBe(2)
})

it('clears selected archive and restore fields on a tenant switch', async () => {
  await render()
  await selectArchive()
  expect(app.container.textContent).toContain('older')
  expect(app.container.textContent).toContain('Restore to chosen time')
  await render('beta')
  expect(app.container.querySelector('select')!.value).toBe('')
  expect(app.container.textContent).not.toContain('older')
  expect(app.container.querySelector('input[type="datetime-local"]')).toBeNull()
})

it('shows a catalog access failure and disables restoring a previously loaded copy', async () => {
  await render()
  await selectArchive()
  unavailable = true
  await act(async () =>
    [...app.container.querySelectorAll('button')]
      .find((item) => item.textContent === 'Refresh backup list')!
      .click(),
  )
  expect(app.container.textContent).toContain('Catalog unavailable')
  expect(
    [...app.container.querySelectorAll('button')].find((item) => item.textContent === 'Restore')!
      .disabled,
  ).toBe(true)
})

it('clears an admin confirmation when the same tenant role drops to Member', async () => {
  retired = true
  await render()
  await selectArchive()
  await act(async () =>
    [...app.container.querySelectorAll('button')]
      .find((item) => item.textContent === 'Forget retired archive')!
      .click(),
  )
  expect(app.container.querySelector('dialog')).not.toBeNull()
  await render('alpha', 'Member')
  expect(app.container.querySelector('dialog')).toBeNull()
  expect(app.container.querySelector('select')!.value).toBe('')
  expect(requests.fetch.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)
})

it('guards switching away from a dirty policy and preserves its draft when discard is canceled', async () => {
  await render()
  const click = async (label: string) =>
    act(async () => {
      ;[...app.container.querySelectorAll('button')]
        .find((item) => item.textContent === label)!
        .click()
    })
  await click('Edit')
  await act(async () => {
    const field = app.container.querySelector('input[value="0 2 * * *"]')!
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(
      field,
      '0 3 * * *',
    )
    field.dispatchEvent(new Event('input', { bubbles: true }))
  })
  await click('Connect storage')
  expect(app.container.textContent).toContain('Discard unsaved changes?')
  await click('Cancel')
  expect(app.container.querySelector('input[value="0 3 * * *"]')).not.toBeNull()
  expect(app.container.textContent).not.toContain('Storage credentials')
  await click('Add policy')
  await click('Discard changes')
  expect(app.container.querySelector('input[value="0 3 * * *"]')).toBeNull()
  expect(app.container.textContent).toContain('Create policy')
})

async function clickButton(label: string) {
  await act(async () => {
    ;[...app.container.querySelectorAll('button')]
      .find((item) => item.textContent === label)!
      .click()
  })
}
async function fill(label: string, value: string) {
  await act(async () => {
    const field = [...app.container.querySelectorAll('input')].find(
      (item) =>
        document.getElementById(item.getAttribute('aria-labelledby') ?? '')?.textContent === label,
    )!
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(field, value)
    field.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

it.each(['unavailable', 'removed'] as const)(
  'rechecks the selected copy when catalog becomes %s during confirmation',
  async (result) => {
    await render()
    await selectArchive()
    await fill('New cluster name', 'restored')
    const pending = deferred<Response>()
    nextCatalogRead = pending.promise
    await clickButton('Refresh backup list')
    await clickButton('Restore')
    expect(app.container.querySelector('dialog')).not.toBeNull()
    await act(async () =>
      pending.resolve(
        result === 'unavailable'
          ? new Response('{"title":"Catalog unavailable"}', { status: 503 })
          : Response.json([]),
      ),
    )
    expect(
      [...app.container.querySelectorAll('button')].find((item) => item.textContent === 'Confirm')!
        .disabled,
    ).toBe(true)
    await clickButton('Confirm')
    expect(restoreAttempts).toHaveLength(0)
  },
)

it.each(['copy', 'time'] as const)('submits a confirmed %s restore from the UI', async (kind) => {
  await render()
  await selectArchive()
  await fill('New cluster name', 'restored')
  if (kind === 'copy') await clickButton('Restore')
  else {
    await fill('Recovery time (local)', '2026-11-12T12:30:00')
    await act(async () =>
      app.container
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
    )
  }
  await clickButton('Confirm')
  expect(restoreAttempts).toHaveLength(1)
  expect(restoreAttempts[0]).toMatchObject({
    name: 'restored',
    confirmed: true,
    idempotencyKey: expect.any(String),
  })
  if (kind === 'copy') {
    expect(restoreAttempts[0].backupId).toBe('older')
    expect(restoreAttempts[0].targetTime).toBeUndefined()
  } else {
    expect(restoreAttempts[0].targetTime).toBe(new Date('2026-11-12T12:30:00').toISOString())
    expect(restoreAttempts[0].backupId).toBeUndefined()
  }
  expect(app.container.querySelector('dialog')).toBeNull()
})

it('retains the same restore command key after an unknown outcome and reopening its confirmation', async () => {
  await render()
  await selectArchive()
  await fill('New cluster name', 'restored')
  rejectNextRestore = true
  await clickButton('Restore')
  await clickButton('Confirm')
  await clickButton('Cancel')
  await clickButton('Restore')
  await clickButton('Confirm')
  expect(restoreAttempts).toHaveLength(2)
  expect(restoreAttempts[0]).toEqual(restoreAttempts[1])
})

it('does not repeat an accepted restore when refreshing metadata fails', async () => {
  await render()
  await selectArchive()
  await fill('New cluster name', 'restored')
  failRefreshAfterRestore = true
  await clickButton('Restore')
  await clickButton('Confirm')
  expect(app.container.querySelector('dialog')).toBeNull()
  expect(app.container.textContent).toContain('Archive refresh unavailable')
  await clickButton('Refresh')
  expect(restoreAttempts).toHaveLength(1)
})

it('loads only the selected operations page and keeps tenant headers on every page', async () => {
  pagedHistory = true
  await render()
  expect(
    app.container.querySelector('[data-testid="backup-operation-new-operation"]'),
  ).not.toBeNull()
  await clickButton('Older operations')
  expect(app.container.querySelector('[data-testid="backup-operation-new-operation"]')).toBeNull()
  expect(
    app.container.querySelector('[data-testid="backup-operation-old-operation"]'),
  ).not.toBeNull()
  await clickButton('Newer operations')
  expect(
    app.container.querySelector('[data-testid="backup-operation-new-operation"]'),
  ).not.toBeNull()
  const historyRequests = requests.fetch.mock.calls.filter(([url]) =>
    String(url).includes('/backups/operations'),
  )
  expect(
    historyRequests.some(
      ([url]) => new URL(String(url)).searchParams.get('after') === 'opaque-older-page',
    ),
  ).toBe(true)
  for (const [, request] of historyRequests) {
    expect(new Headers(request?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
    expect(new Headers(request?.headers).get('Authorization')).toBe('Bearer current')
  }
})

it('renders UTC and local next runs across the repeated daylight-saving hour', async () => {
  const local = new Intl.DateTimeFormat('uk-UA', {
    timeZone: 'Europe/Kyiv',
    dateStyle: 'short',
    timeStyle: 'short',
  })
  vi.spyOn(Date.prototype, 'toLocaleString').mockImplementation(function (this: Date) {
    return local.format(this)
  })
  nextScheduledAt = '2026-10-25T00:30:00Z'
  await render()
  expect(app.container.textContent).toContain('2026-10-25T00:30:00.000Z · 25.10.26, 03:30')
  nextScheduledAt = '2026-10-25T01:30:00Z'
  await clickButton('Refresh')
  expect(app.container.textContent).toContain('2026-10-25T01:30:00.000Z · 25.10.26, 03:30')
  nextScheduledAt = '2026-03-29T01:30:00Z'
  await clickButton('Refresh')
  expect(app.container.textContent).toContain('2026-03-29T01:30:00.000Z · 29.03.26, 04:30')
})
