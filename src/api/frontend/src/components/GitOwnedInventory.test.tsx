// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createTestApp } from '../test/TestApp'
import { resourceGroup, webService } from '../test/fixtures'
import { WebServicesPage } from './WebServicesPage'
import { ResourceGroupsPage } from './ResourceGroupsPage'

let app: ReturnType<typeof createTestApp>
const owned = {
  sourceId: 'source',
  sourceUrl: 'https://github.com/example/repo',
  blocker: 'SecretValuesMissing',
  pendingRemoval: false,
  configurationApplied: false,
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
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
  app = createTestApp()
})
afterEach(() => {
  act(() => app.root.unmount())
  app.container.remove()
})

it('keeps Git-owned services inspectable and prevents a stale catalog deletion', async () => {
  const item = webService()
  const onDelete = vi.fn()
  const render = (gitOps = item.gitOps, status: 'success' | 'error' = 'success') =>
    act(() =>
      app.root.render(
        <WebServicesPage
          items={[{ ...item, gitOps }]}
          resourceGroups={[]}
          status={status}
          page={null}
          pageNumber={1}
          pageSize={10}
          search=""
          onPage={vi.fn()}
          onPageSize={vi.fn()}
          onSearch={vi.fn()}
          deletingId={null}
          canManage
          onRefresh={vi.fn()}
          onCreate={vi.fn()}
          onOpen={vi.fn()}
          resourceHref={() => '/service'}
          onDelete={onDelete}
        />,
      ),
    )
  render()
  await act(async () =>
    app.container
      .querySelector<HTMLButtonElement>(`button[aria-label="Delete ${item.name}"]`)!
      .click(),
  )
  expect(app.container.querySelector('dialog')).not.toBeNull()
  render(owned)
  expect(
    app.container.querySelector<HTMLButtonElement>(`button[aria-label="Delete ${item.name}"]`)!
      .disabled,
  ).toBe(true)
  const confirm = [...app.container.querySelectorAll<HTMLButtonElement>('dialog button')].find(
    (button) => button.textContent === 'Delete service',
  )!
  expect(confirm.disabled).toBe(true)
  await act(async () => confirm.click())
  expect(onDelete).not.toHaveBeenCalled()
  expect(app.container.textContent).toContain('Inspect')
})

it('disables deletion of owned groups while keeping manual groups editable', () => {
  const group = resourceGroup()
  const render = (gitOps = group.gitOps) =>
    act(() =>
      app.root.render(
        <ResourceGroupsPage
          items={[{ ...group, gitOps }]}
          page={null}
          status="success"
          error=""
          pageNumber={1}
          pageSize={10}
          search=""
          canManage
          deletingId={null}
          deleteError=""
          onRefresh={vi.fn()}
          onCreate={vi.fn()}
          onOpen={vi.fn()}
          resourceHref={() => '/group'}
          onDelete={vi.fn()}
          onPage={vi.fn()}
          onPageSize={vi.fn()}
          onSearch={vi.fn()}
        />,
      ),
    )
  render()
  expect(
    app.container.querySelector<HTMLButtonElement>(`button[aria-label="Delete ${group.name}"]`)!
      .disabled,
  ).toBe(false)
  expect(app.container.textContent).toContain('Edit')
  render(owned)
  expect(
    app.container.querySelector<HTMLButtonElement>(`button[aria-label="Delete ${group.name}"]`)!
      .disabled,
  ).toBe(true)
  expect(app.container.textContent).toContain('Inspect')
})
