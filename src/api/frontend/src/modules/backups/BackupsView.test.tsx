// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import type { AppServices } from '../../shell/contracts'
import { createTestApp } from '../../test/TestApp'
import { strictFetch } from '../../test/http'
import { BackupsView } from './BackupsView'

const requests = strictFetch()
let app: ReturnType<typeof createTestApp>
let unavailable = false
let retired = false
let policyRevision = 1
let submittedRevision: number | undefined
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
  submittedRevision = undefined
  app = createTestApp()
  requests.reset()
  vi.stubGlobal('fetch', requests.fetch)
  requests.fetch.mockImplementation((input, init) => {
    const path = new URL(String(input)).pathname
    if (path === '/api/v1/backups/policies/policy' && init?.method === 'PUT') {
      submittedRevision = JSON.parse(String(init.body)).expectedRevision
      return Promise.resolve(Response.json({ id: 'policy', revision: 3 }))
    }
    if (path === '/api/v1/backups/policies')
      return Promise.resolve(
        Response.json([
          {
            id: 'policy',
            name: 'nightly',
            revision: policyRevision,
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
