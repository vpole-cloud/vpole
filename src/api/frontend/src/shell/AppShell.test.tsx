// @vitest-environment jsdom
import { act, lazy, useEffect, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AppShell } from './AppShell'
import { useAppServices } from './AppContext'
import { createAppRegistry } from './moduleRegistry'
import { ConfirmationDialog, Field } from '../ui/components'
import type { AppModule, AppServices } from './contracts'
import type { Tenant } from '../types'
import { readIntegrationNavigation } from '../integrationsState'

let container: HTMLDivElement
let root: Root
const auth: AppServices['auth'] = {
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

describe('app module composition', () => {
  it('keeps control names separate from hints and preserves existing validation descriptions', async () => {
    await act(async () => root.render(<><p id="validation">Choose an enabled target.</p><Field label="Target" hint="Inventory belongs to this cluster.">
      <select aria-describedby="validation"><option>East</option></select>
    </Field></>))
    const control = container.querySelector('select')!
    const name = document.getElementById(control.getAttribute('aria-labelledby')!)!.textContent
    expect(name).toBe('Target')
    const descriptions = control.getAttribute('aria-describedby')!.split(' ').map((id) => document.getElementById(id)?.textContent)
    expect(descriptions).toEqual(['Choose an enabled target.', 'Inventory belongs to this cluster.'])
  })
  it('preserves explicit accessible names while attaching field hints', async () => {
    await act(async () => root.render(<><p id="custom-name">Group page size</p>
      <Field label="Per page" hint="Service inventory size."><select aria-label="Services per page"><option>8</option></select></Field>
      <Field label="Per page" hint="Group inventory size."><select aria-labelledby="custom-name"><option>8</option></select></Field>
    </>))
    const [services, groups] = [...container.querySelectorAll('select')]
    expect(services.getAttribute('aria-label')).toBe('Services per page')
    expect(services.hasAttribute('aria-labelledby')).toBe(false)
    expect(groups.getAttribute('aria-labelledby')).toBe('custom-name')
    expect(document.getElementById(services.getAttribute('aria-describedby')!)?.textContent).toBe('Service inventory size.')
    expect(document.getElementById(groups.getAttribute('aria-describedby')!)?.textContent).toBe('Group inventory size.')
  })
  it('names controls with validation or action siblings independently of hint and error text', async () => {
    await act(async () => root.render(<>
      <Field label="Name" hint="Use lowercase letters."><input />{false}</Field>
      <Field label="Value" hint="Values stay write-only."><input aria-describedby="value-error" />
        <span id="value-error">A value is required.</span><button type="button">Use empty value</button></Field>
      <Field label="Per page" hint="Service inventory size."><select aria-label="Services per page"><option>8</option></select><span>Validation</span></Field>
    </>))
    const [name, value] = [...container.querySelectorAll('input')]
    expect(document.getElementById(name.getAttribute('aria-labelledby')!)?.textContent).toBe('Name')
    expect(document.getElementById(name.getAttribute('aria-describedby')!)?.textContent).toBe('Use lowercase letters.')
    expect(document.getElementById(value.getAttribute('aria-labelledby')!)?.textContent).toBe('Value')
    expect(value.getAttribute('aria-describedby')!.split(' ').map((id) => document.getElementById(id)?.textContent))
      .toEqual(['A value is required.', 'Values stay write-only.'])
    const select = container.querySelector('select')!
    expect(select.getAttribute('aria-label')).toBe('Services per page')
    expect(select.hasAttribute('aria-labelledby')).toBe(false)
    expect(document.getElementById(select.getAttribute('aria-describedby')!)?.textContent).toBe('Service inventory size.')
    expect(container.querySelector('button')!.textContent).toBe('Use empty value')
  })
  it('loads a deferred module in the current tenant after a scope change', async () => {
    let release!: () => void
    const pending = new Promise<void>((resolve) => { release = resolve })
    const observed: string[] = []
    const Deferred = lazy(async () => {
      await pending
      return { default: function Loaded() {
        const { tenant } = useAppServices()
        observed.push(tenant.active!.id)
        return <p>Deferred tenant: {tenant.active!.id}</p>
      } }
    })
    const registry = createAppRegistry([{ id: 'deferred', views: [{ id: 'deferred', label: 'Deferred', component: Deferred, access: 'tenant' }] }])
    const render = (id: string) => root.render(<AppShell registry={registry} auth={auth} api={{ baseUrl: '/api' }} tenants={tenants(id)} initialViewId="deferred" />)
    await act(async () => render('alpha'))
    expect(container.textContent).toContain('Loading view…')
    await act(async () => render('beta'))
    await act(async () => { release() })
    expect(container.textContent).toContain('Deferred tenant: beta')
    expect(observed).not.toContain('alpha')
  })

  it('contains a failed module download and permits navigation to another view', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const Broken = lazy(async () => { throw new Error('internal chunk diagnostic') })
    const registry = createAppRegistry([{ id: 'home', views: [
      { id: 'broken', label: 'Broken', component: Broken, access: 'public' },
      { id: 'overview', label: 'Overview', component: () => <p>Available view</p>, access: 'public' },
    ] }])
    await act(async () => root.render(<AppShell registry={registry} auth={auth} api={{ baseUrl: '/api' }} tenants={tenants('alpha')} initialViewId="broken" />))
    expect(container.textContent).toContain('This view could not be loaded.')
    expect(container.textContent).not.toContain('internal chunk diagnostic')
    await act(async () => button('Overview').click())
    expect(container.textContent).toContain('Available view')
  })
  it('resolves typed resource URLs through an independent registration even when the view hint is stale', async () => {
    window.history.replaceState({}, '', '/?view=old-view&resourceType=test%2Fwidget&resource=widget&tenant=alpha')
    function Widget() { const { navigation } = useAppServices(); return <p>Widget {new URLSearchParams(navigation.search).get('resource')}</p> }
    const registry = createAppRegistry([{ id: 'widgets', views: [{ id: 'widgets', label: 'Widgets', component: Widget, access: 'tenant' }], resourceTypes: [{ id: 'test/widget', label: 'Widget', viewId: 'widgets' }] }])
    await act(async () => root.render(<AppShell registry={registry} auth={auth} api={{ baseUrl: '/api' }} tenants={tenants('alpha')} initialViewId="widgets" />))
    expect(container.textContent).toContain('Widget widget')
    expect(new URLSearchParams(window.location.search).get('view')).toBe('widgets')
  })
  it('reports an unregistered URL view explicitly and clears its resource selection on recovery', async () => {
    window.history.replaceState({}, '', '/?view=removed-provider&tenant=alpha&resource=old-resource')
    const registry = createAppRegistry([{ id: 'home', views: [
      { id: 'overview', label: 'Overview', component: () => <p>Home</p>, access: 'public' },
    ] }])
    await act(async () => { root.render(<AppShell registry={registry} auth={auth} api={{ baseUrl: '/api' }}
      tenants={tenants('alpha')} initialViewId="overview" />) })
    expect(container.textContent).toContain('This view is unavailable.')
    await act(async () => { button('Open overview').click() })
    expect(container.textContent).toContain('Home')
    expect(new URLSearchParams(window.location.search).has('resource')).toBe(false)
  })

  it('navigates an independent module and passes current services to its view and command', async () => {
    const command = vi.fn()
    function ExtraView() {
      const { api, tenant: activeTenant, theme } = useAppServices()
      return <p>{api.baseUrl} / {activeTenant.active?.id} / {theme.value}</p>
    }
    const extra: AppModule = {
      id: 'extra', views: [{ id: 'extra', label: 'Test resource', component: ExtraView, access: 'tenant' }],
      resourceTypes: [{ id: 'test/resource', label: 'Test', viewId: 'extra' }],
      commands: [{ id: 'test.command', label: 'Run test', viewId: 'extra', run: command }],
    }
    const registry = createAppRegistry([
      { id: 'home', views: [{ id: 'overview', label: 'Overview', component: () => <p>Home</p>, access: 'public' }] }, extra,
    ])
    await act(async () => { root.render(<AppShell registry={registry} auth={auth} api={{ baseUrl: '/api', token: 'test-token' }}
      tenants={tenants('alpha')} initialViewId="overview" />) })
    expect(container.querySelectorAll('.tenant-selector select')).toHaveLength(1)
    expect(container.querySelector('.tenant-selector option')?.textContent).toBe('alpha · Owner')
    expect(container.textContent).not.toContain('Active tenant')
    expect(container.querySelector('.side-nav__footer')).toBeNull()
    await act(async () => { button('Test resource').click() })
    expect(container.textContent).toContain('/api / alpha / light')
    expect(window.location.search).toBe('?view=extra&tenant=alpha')
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
    const first: AppModule = { id: 'first', views: [view] }
    const second: AppModule = { id: 'second', views: [{ ...view, id: 'second-view' }] }
    if (category === 'module') second.id = first.id
    if (category === 'view') second.views = [view]
    if (category === 'resource type') first.resourceTypes = second.resourceTypes = [{ id: 'resource', label: 'Resource', viewId: view.id }]
    if (category === 'command') first.commands = second.commands = [{ id: 'command', label: 'Command', viewId: view.id, run: () => {} }]
    expect(() => createAppRegistry([first, second])).toThrow(`Duplicate app ${category} identifier`)
  })

  it('rejects a resource registration pointing at a missing view', () => {
    expect(() => createAppRegistry([{ id: 'broken', views: [],
      resourceTypes: [{ id: 'resource', label: 'Resource', viewId: 'missing' }] }])).toThrow('references missing view missing')
  })

  it('remounts tenant views and ignores previous-tenant results and notifications', async () => {
    let completeOld!: () => void
    const oldResult = new Promise<void>((resolve) => { completeOld = resolve })
    function AsyncView() {
      const services = useAppServices()
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
    const registry = createAppRegistry([{ id: 'async', views: [{ id: 'async', label: 'Async', component: AsyncView, access: 'tenant' }] }])
    const render = (id: string) => root.render(<AppShell registry={registry} auth={auth} api={{ baseUrl: '/api' }}
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
    const registry = createAppRegistry([{ id: 'private', views: [{ id: 'private', label: 'Private', component: view, access: 'tenant' }] }])
    await act(async () => { root.render(<AppShell registry={registry} auth={{ ...auth, authenticated: false }}
      api={{ baseUrl: '/api' }} tenants={tenants('alpha')} initialViewId="private" />) })
    expect(view).not.toHaveBeenCalled()
    expect(container.textContent).toContain('Sign in to continue.')
  })

  it('clears tenant context and existing notifications when the account logs out', async () => {
    function PublicProbe() {
      const { tenant: activeTenant, notifications } = useAppServices()
      const publish = notifications.publish
      const id = activeTenant.active?.id
      useEffect(() => { if (id) publish(`Notice for ${id}`) }, [id, publish])
      return <p>Context: {id ?? 'guest'}</p>
    }
    const registry = createAppRegistry([{ id: 'public', views: [{ id: 'public', label: 'Public', component: PublicProbe, access: 'public' }] }])
    const render = (authenticated: boolean) => root.render(<AppShell registry={registry} auth={{ ...auth, authenticated }}
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

it('opens global commands by Ctrl+K and closes with Escape while restoring header focus', async () => {
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } },
    close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } },
  })
  const registry = createAppRegistry([{ id: 'home', views: [{ id: 'overview', label: 'Overview', component: () => <p>Home</p>, access: 'public' }] }])
  await act(async () => root.render(<AppShell registry={registry} auth={auth} api={{ baseUrl: '/api' }} tenants={tenants('alpha')} initialViewId="overview" />))
  const opener = container.querySelector<HTMLButtonElement>('[aria-label="Search and commands"]')!; opener.focus()
  await act(async () => window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true })))
  expect(container.querySelector('dialog')).not.toBeNull(); expect(document.activeElement?.getAttribute('role')).toBe('combobox')
  await act(async () => container.querySelector('dialog')!.dispatchEvent(new Event('cancel', { bubbles: true, cancelable: true })))
  expect(container.querySelector('dialog')).toBeNull(); expect(document.activeElement).toBe(opener)
})
