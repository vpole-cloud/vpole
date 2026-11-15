// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { strictFetch } from '../../test/http'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { seaweedFSModule } from './SeaweedFSModule'
import { SeaweedFSView } from './SeaweedFSView'

const registry = createAppRegistry([
  {
    ...seaweedFSModule,
    views: seaweedFSModule.views.map((view) => ({ ...view, component: SeaweedFSView })),
  },
])
const requests = strictFetch()
const instance = {
  id: 'instance',
  name: 'media',
  resourceGroupId: 'group',
  spec: { mode: 'SingleNode', storage: { sizeGb: 1, storageProfileId: 'disk' } },
  tags: [],
  placement: null,
  revision: 1,
  lifecycle: 'Active',
}
let root: Root
let container: HTMLDivElement
let reveal: Promise<Response>
const json = (value: unknown) => Promise.resolve(Response.json(value))
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear()
  window.history.replaceState({}, '', '/?view=seaweedfs&resource=instance')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  requests.reset()
  reveal = json({ accessKey: 'access-test', secretKey: 'only-test-value' })
  requests.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    const path = url.pathname
    if (path.endsWith('/resource-groups'))
      return json({
        items: [{ id: 'group', name: 'Apps' }],
        totalCount: 1,
        pageNumber: 1,
        pageSize: 200,
      })
    if (path.endsWith('/isolation-scopes'))
      return json([
        {
          id: 'scope',
          name: 'Default',
          resourceGroupId: 'group',
          lifecycle: 'active',
          isDefault: true,
        },
      ])
    if (path.endsWith('/deployment-targets/available'))
      return json([
        { id: 'east', displayName: 'East' },
        { id: 'west', displayName: 'West' },
      ])
    if (path.endsWith('/compute-profiles/available'))
      return json([{ id: 'cpu', displayName: 'Small', isDefault: true }])
    if (path.endsWith('/storage-profiles'))
      return json([{ id: 'disk', displayName: 'SSD', isDefault: true }])
    if (path.endsWith('/seaweedfs/capacity'))
      return json({
        storageGb: 6,
        workloadInstances: url.searchParams.get('usesExistingPostgres') === 'true' ? 12 : 15,
      })
    if (path.endsWith('/managed-database-candidates')) {
      const target = url.searchParams.get('deploymentTargetId')
      return json(
        target === 'east'
          ? [{ id: 'database', name: 'Shared HA database', deploymentTargetId: target }]
          : [],
      )
    }
    if (path.endsWith('/seaweedfs/'))
      return json({ items: [instance], pageNumber: 1, pageSize: 20, totalCount: 1 })
    if (path.endsWith('/seaweedfs/instance')) return json(instance)
    if (path.endsWith('/instance/buckets'))
      return json([
        { id: 'bucket', name: 'assets', revision: 1, lifecycle: 'Active', secretId: 'secret' },
      ])
    if (path.endsWith('/instance/credentials')) return reveal
    if (path.endsWith('/resource-connections/endpoints'))
      return json({
        availability: 'Supported',
        endpoints: [],
        topology: { status: 'NotEvaluated', reasons: [] },
      })
    return requests.unhandled(input, init)
  })
  vi.stubGlobal('fetch', requests.fetch)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  vi.unstubAllGlobals()
  requests.verify()
})
async function render(role: 'Owner' | 'Admin' | 'Member') {
  const tenant = { id: 'alpha', name: 'Alpha', role, isDefault: true }
  await act(async () =>
    root.render(
      <AppShell
        registry={registry}
        initialViewId="seaweedfs"
        api={{ baseUrl: 'http://localhost', token: 'test-token' }}
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
          activeTenantId: tenant.id,
          selectTenant: () => {},
          status: 'success',
          error: '',
        }}
      />,
    ),
  )
}
const button = (label: string) =>
  [...container.querySelectorAll('button')].find(
    (element) => element.textContent === label && !element.closest('[hidden]'),
  )
it('hides revealed credentials on blur and reserves administrator reveal for the owner', async () => {
  await render('Owner')
  await act(async () => button('Show administrator credentials')!.click())
  expect(container.textContent).toContain('only-test-value')
  await act(async () => {
    window.dispatchEvent(new Event('blur'))
  })
  expect(container.textContent).not.toContain('only-test-value')
  await render('Admin')
  expect(button('Show administrator credentials')).toBeUndefined()
  expect(button('Show bucket credentials')).toBeDefined()
})
it('discards an outstanding administrator reveal when authorization changes', async () => {
  await render('Owner')
  let resolve!: (value: Response) => void
  reveal = new Promise<Response>((done) => {
    resolve = done
  })
  await act(async () => button('Show administrator credentials')!.click())
  await render('Member')
  await act(async () => {
    resolve(Response.json({ accessKey: 'old', secretKey: 'former-owner-value' }))
    await reveal
  })
  expect(container.textContent).not.toContain('former-owner-value')
  expect(button('Show administrator credentials')).toBeUndefined()
})

it('defaults to dedicated metadata and clears an existing cluster selection when the target changes', async () => {
  window.history.replaceState({}, '', '/?view=seaweedfs&resource=create')
  await render('Owner')
  function field(label: string) {
    const parent = [...container.querySelectorAll('label')].find(
      (item) =>
        !item.closest('[hidden]') && item.querySelector('.field__label')?.textContent === label,
    )
    return parent!.querySelector('select')!
  }
  async function select(label: string, value: string) {
    await act(async () => {
      const control = field(label)
      control.value = value
      control.dispatchEvent(new Event('change', { bubbles: true }))
    })
  }
  await select('Deployment target', 'east')
  await select('Deployment mode', 'Replicated')
  expect(field('Filer metadata storage').value).toBe('dedicated')
  await select('Filer metadata storage', 'existing')
  expect(button('Create SeaweedFS')?.disabled).toBe(true)
  await select('PostgreSQL cluster', 'database')
  expect(button('Create SeaweedFS')?.disabled).toBe(false)
  expect(container.textContent).toContain('12 workload pods requested')
  await select('Deployment target', 'west')
  expect(field('PostgreSQL cluster').value).toBe('')
  expect(container.textContent).toContain('No eligible PostgreSQL clusters')
  expect(button('Create SeaweedFS')?.disabled).toBe(true)
})
