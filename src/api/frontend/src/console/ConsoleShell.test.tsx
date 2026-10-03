// @vitest-environment jsdom
import { act, useEffect, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ConsoleShell } from './ConsoleShell'
import { useConsoleServices } from './ConsoleContext'
import { createConsoleRegistry } from './moduleRegistry'
import { ConfirmationDialog } from './components'
import type { ConsoleModule, ConsoleServices } from './contracts'
import type { Tenant } from '../types'
import { readIntegrationNavigation } from '../integrationsState'

let container: HTMLDivElement
let root: Root
const auth: ConsoleServices['auth'] = {
  initialized: true, authenticated: true, userId: 'user-a', userName: 'Developer',
  isPlatformAdmin: false, login: vi.fn(), logout: vi.fn(),
}
const tenant = (id: string): Tenant => ({ id, name: id, role: 'Owner', isDefault: true })
function tenants(id: string) {
  return { items: [tenant(id)], activeTenant: tenant(id), activeTenantId: id,
    status: 'success' as const, error: '', selectTenant: vi.fn() }
}
const button = (label: string) => [...container.querySelectorAll('button')].find((node) => node.textContent === label)!
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear()
  window.history.replaceState({}, '', '/')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})
afterEach(async () => {
  await act(async () => { root.unmount() })
  container.remove()
  vi.restoreAllMocks()
})

describe('console module composition', () => {
  it('navigates an independent module and passes current services to its view and command', async () => {
    const command = vi.fn()
    function ExtraView() {
      const { api, tenant: activeTenant, theme } = useConsoleServices()
      return <p>{api.baseUrl} / {activeTenant.active?.id} / {theme.value}</p>
    }
    const extra: ConsoleModule = {
      id: 'extra', views: [{ id: 'extra', label: 'Test resource', component: ExtraView, access: 'tenant' }],
      resourceTypes: [{ id: 'test/resource', label: 'Test', viewId: 'extra' }],
      commands: [{ id: 'test.command', label: 'Run test', viewId: 'extra', run: command }],
    }
    const registry = createConsoleRegistry([
      { id: 'home', views: [{ id: 'overview', label: 'Overview', component: () => <p>Home</p>, access: 'public' }] }, extra,
    ])
    await act(async () => { root.render(<ConsoleShell registry={registry} auth={auth} api={{ baseUrl: '/api', token: 'test-token' }}
      tenants={tenants('alpha')} initialViewId="overview" />) })
    await act(async () => { button('Test resource').click() })
    expect(container.textContent).toContain('/api / alpha / light')
    expect(window.location.search).toBe('?view=extra')
    expect(readIntegrationNavigation('?view=extra', registry.views.map((view) => view.id)).view).toBe('extra')
    await act(async () => { button('Run test').click() })
    expect(command).toHaveBeenCalledWith(expect.objectContaining({ api: { baseUrl: '/api', token: 'test-token', tenantId: 'alpha' } }))
    await act(async () => { button('Dark theme').click() })
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(container.textContent).toContain('/api / alpha / dark')
    expect(localStorage.getItem('pole.console.theme')).toBe('dark')
  })

  it.each(['module', 'view', 'resource type', 'command'])('rejects duplicate %s identifiers with a useful diagnostic', (category) => {
    const view = { id: 'view', label: 'View', component: () => null, access: 'public' as const }
    const first: ConsoleModule = { id: 'first', views: [view] }
    const second: ConsoleModule = { id: 'second', views: [{ ...view, id: 'second-view' }] }
    if (category === 'module') second.id = first.id
    if (category === 'view') second.views = [view]
    if (category === 'resource type') first.resourceTypes = second.resourceTypes = [{ id: 'resource', label: 'Resource', viewId: view.id }]
    if (category === 'command') first.commands = second.commands = [{ id: 'command', label: 'Command', viewId: view.id, run: () => {} }]
    expect(() => createConsoleRegistry([first, second])).toThrow(`Duplicate console ${category} identifier`)
  })

  it('rejects a resource registration pointing at a missing view', () => {
    expect(() => createConsoleRegistry([{ id: 'broken', views: [],
      resourceTypes: [{ id: 'resource', label: 'Resource', viewId: 'missing' }] }])).toThrow('references missing view missing')
  })

  it('remounts tenant views and ignores previous-tenant results and notifications', async () => {
    let completeOld!: () => void
    const oldResult = new Promise<void>((resolve) => { completeOld = resolve })
    function AsyncView() {
      const services = useConsoleServices()
      const id = services.tenant.active!.id
      const publish = services.notifications.publish
      const [value, setValue] = useState('loading')
      useEffect(() => {
        void (id === 'alpha' ? oldResult : Promise.resolve()).then(() => {
          setValue(id)
          publish(`Loaded ${id}`)
        })
        // This test provider deliberately leaves its old read running to exercise the shell boundary.
      }, [id, publish])
      return <p>Result: {value}</p>
    }
    const registry = createConsoleRegistry([{ id: 'async', views: [{ id: 'async', label: 'Async', component: AsyncView, access: 'tenant' }] }])
    const render = (id: string) => root.render(<ConsoleShell registry={registry} auth={auth} api={{ baseUrl: '/api' }}
      tenants={tenants(id)} initialViewId="async" />)
    await act(async () => { render('alpha') })
    await act(async () => { render('beta') })
    expect(container.textContent).toContain('Result: beta')
    await act(async () => { completeOld() })
    expect(container.textContent).toContain('Result: beta')
    expect(container.textContent).toContain('Loaded beta')
    expect(container.textContent).not.toContain('Loaded alpha')
  })

  it('gates tenant views after logout', async () => {
    const view = vi.fn(() => <p>Private resource</p>)
    const registry = createConsoleRegistry([{ id: 'private', views: [{ id: 'private', label: 'Private', component: view, access: 'tenant' }] }])
    await act(async () => { root.render(<ConsoleShell registry={registry} auth={{ ...auth, authenticated: false }}
      api={{ baseUrl: '/api' }} tenants={tenants('alpha')} initialViewId="private" />) })
    expect(view).not.toHaveBeenCalled()
    expect(container.textContent).toContain('Sign in to continue.')
  })

  it('clears tenant context and existing notifications when the account logs out', async () => {
    function PublicProbe() {
      const { tenant: activeTenant, notifications } = useConsoleServices()
      const publish = notifications.publish
      const id = activeTenant.active?.id
      useEffect(() => { if (id) publish(`Notice for ${id}`) }, [id, publish])
      return <p>Context: {id ?? 'guest'}</p>
    }
    const registry = createConsoleRegistry([{ id: 'public', views: [{ id: 'public', label: 'Public', component: PublicProbe, access: 'public' }] }])
    const render = (authenticated: boolean) => root.render(<ConsoleShell registry={registry} auth={{ ...auth, authenticated }}
      api={{ baseUrl: '/api' }} tenants={tenants('alpha')} initialViewId="public" />)
    await act(async () => { render(true) })
    expect(container.textContent).toContain('Notice for alpha')
    await act(async () => { render(false) })
    expect(container.textContent).not.toContain('Notice for alpha')
    expect(container.textContent).toContain('Context: guest')
  })
})

it('focuses cancel and restores focus after keyboard dismissal of a confirmation', async () => {
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } },
    close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } },
  })
  function DialogProbe() {
    const [open, setOpen] = useState(false)
    return <><button onClick={() => setOpen(true)}>Delete resource</button>{open &&
      <ConfirmationDialog title="Delete resource?" confirmLabel="Delete" onCancel={() => setOpen(false)} onConfirm={() => {}}>
        This removes the resource.
      </ConfirmationDialog>}</>
  }
  await act(async () => { root.render(<DialogProbe />) })
  button('Delete resource').focus()
  await act(async () => { button('Delete resource').click() })
  expect(document.activeElement?.textContent).toBe('Cancel')
  await act(async () => { container.querySelector('dialog')!.dispatchEvent(new Event('cancel', { cancelable: true })) })
  expect(container.querySelector('dialog')).toBeNull()
  expect(document.activeElement?.textContent).toBe('Delete resource')
})
