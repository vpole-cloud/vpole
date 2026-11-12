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
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  unavailable = false
  retired = false
  app = createTestApp()
  requests.reset()
  vi.stubGlobal('fetch', requests.fetch)
  requests.fetch.mockImplementation((input, init) => {
    const path = new URL(String(input)).pathname
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
      return Promise.resolve(Response.json([]))
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
