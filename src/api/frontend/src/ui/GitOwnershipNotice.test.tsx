// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createTestApp } from '../test/TestApp'
import { AppContext } from '../shell/AppContext'
import { gitOpsTestServices } from '../modules/gitops/testServices'
import { gitOpsSourceResourceType } from '../data/gitOps'
import { GitOwnershipNotice } from './GitOwnershipNotice'
let app: ReturnType<typeof createTestApp>
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  app = createTestApp()
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
})
const status = {
  sourceId: 'source',
  sourceUrl: '/api/v1/gitops/sources/source',
  blocker: null,
  pendingRemoval: false,
  configurationApplied: true,
}
it('opens the registered tenant console source instead of the backend API', async () => {
  const services = gitOpsTestServices({
    resourceTypes: [{ id: gitOpsSourceResourceType, label: 'GitOps source', viewId: 'gitops' }],
  })
  const open = vi.fn()
  services.navigation.openResource = open
  services.navigation.resourceHref = () => '/?tenant=tenant&view=gitops&resource=source'
  await act(async () =>
    app.root.render(
      <AppContext.Provider value={services}>
        <GitOwnershipNotice status={status} />
      </AppContext.Provider>,
    ),
  )
  const link = app.container.querySelector<HTMLAnchorElement>('a')!
  expect(link.getAttribute('href')).toContain('view=gitops')
  expect(link.getAttribute('href')).not.toContain('/api/')
  await act(async () => link.click())
  expect(open).toHaveBeenCalledWith({
    resourceTypeId: gitOpsSourceResourceType,
    resourceId: 'source',
  })
})
it('keeps identity inspectable when a host has no matching console view', async () => {
  await act(async () => app.root.render(<GitOwnershipNotice status={status} />))
  expect(app.container.querySelector('a')).toBeNull()
  expect(app.container.textContent).toContain('source')
})
