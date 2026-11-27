// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { strictFetch, jsonResponse } from '../../test/http'
import { gitOpsModule } from './GitOpsModule'
import { GitOpsView } from './GitOpsView'
import { gitOpsSourceFixture } from './testFixtures'
import type { GitOpsSource } from './types'
const registry = createAppRegistry([
  {
    ...gitOpsModule,
    views: gitOpsModule.views.map((view) => ({ ...view, component: GitOpsView })),
  },
])
const requests = strictFetch()
let root: Root
let container: HTMLDivElement
let source: GitOpsSource
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperty(document, 'hidden', { configurable: true, value: false })
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true
      },
    },
    close: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false
      },
    },
  })
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
    callback(0)
    return 1
  })
  localStorage.clear()
  window.history.replaceState({}, '', '/?view=gitops&resource=catalog')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  source = structuredClone(gitOpsSourceFixture)
  requests.reset()
  vi.stubGlobal('fetch', requests.fetch)
  requests.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input))
    const path = url.pathname.replace(/\/$/, '')
    if (path === '/api/v1/gitops/sources')
      return jsonResponse({ items: [source], page: 1, pageSize: 20, hasMore: false })
    if (path === '/api/v1/gitops/sources/source') return jsonResponse(source)
    if (path === '/api/v1/gitops/sources/source/resources')
      return jsonResponse({
        items: [
          {
            kind: 'ResourceGroup',
            name: 'group',
            resourceId: 'group',
            resourceTypeId: null,
            materialization: 'Created',
            pendingRemoval: true,
            blocker: null,
            lastRunId: 'run',
          },
        ],
        page: 1,
        pageSize: 50,
        hasMore: false,
        inventoryRevision: source.inventory.revision,
      })
    return requests.unhandled(input, init)
  })
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  requests.verify()
  vi.unstubAllGlobals()
})
async function render(role: 'Owner' | 'Viewer' = 'Owner') {
  const tenant = { id: 'tenant', name: 'Tenant', role, isDefault: true }
  await act(async () =>
    root.render(
      <AppShell
        registry={registry}
        initialViewId="gitops"
        api={{ baseUrl: 'https://api.test', token: 'token' }}
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
const visible = (node: Element) => !node.closest('[hidden]')
async function section(name: string) {
  await act(async () =>
    [
      ...container.querySelectorAll<HTMLButtonElement>(
        '[aria-label="GitOps source sections"] [role="tab"]',
      ),
    ]
      .find((node) => visible(node) && node.textContent === name)!
      .click(),
  )
}
it('opens a source workspace and preserves dirty settings across sections without persisting drafts', async () => {
  await render()
  expect(container.querySelector('[data-testid="resource-row-source"]')).not.toBeNull()
  await act(async () =>
    container.querySelector<HTMLAnchorElement>('[data-testid="resource-row-source"] a')!.click(),
  )
  expect(window.location.search).toContain('resource=source')
  expect(container.querySelector('[data-testid="gitops-source-source"]')).not.toBeNull()
  await section('Settings')
  let input = container.querySelector<HTMLInputElement>(
    '[id$="panel-settings"] input[type="number"]',
  )!
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, '600')
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
  await section('Resources')
  expect(container.textContent).toContain('ownership retained')
  await section('Settings')
  input = container.querySelector<HTMLInputElement>('[id$="panel-settings"] input[type="number"]')!
  expect(input.value).toBe('600')
  expect(container.querySelector('dialog[open]')).toBeNull()
  const stored = localStorage.getItem('pole.gitops.workspace.v1.user.tenant')!
  expect(stored).toContain('source')
  expect(stored).not.toContain('600')
  expect(stored).not.toContain('github.com')
})
it('keeps tenant viewer source navigation read-only', async () => {
  window.history.replaceState({}, '', '/?view=gitops&resource=source&section=overview')
  await render('Viewer')
  const sync = [...container.querySelectorAll<HTMLButtonElement>('button')].find(
    (node) => visible(node) && node.textContent === 'Sync',
  )!
  expect(sync.disabled).toBe(true)
  await act(async () => sync.click())
  expect(
    requests.fetch.mock.calls.every(([, init]) => !init?.method || init.method === 'GET'),
  ).toBe(true)
  expect(container.textContent).toContain('read-only')
})
