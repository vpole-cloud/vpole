// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import { createTestApp } from '../../test/TestApp'
import { deferred, jsonResponse, strictFetch } from '../../test/http'
import { GitOpsPreviews } from './GitOpsPreviews'
import { previewFixture } from './previewFixtures'
import { gitOpsSourceFixture } from './testFixtures'
import { gitOpsTestServices } from './testServices'
import type { GitOpsPreview, GitOpsSource, PreviewRequest, SourceResource } from './types'
const requests = strictFetch()
let app: ReturnType<typeof createTestApp>
let services: ReturnType<typeof gitOpsTestServices>
let source: GitOpsSource
let active: boolean
let metadata: GitOpsPreview | null
let createResponse: ((request: PreviewRequest) => Promise<Response>) | null
const submitted: PreviewRequest[] = []
const onRun = vi.fn()
const resource: SourceResource = {
  kind: 'RedisService',
  name: 'cache',
  resourceId: 'cache-id',
  resourceTypeId: 'pole/redis',
  materialization: 'Created',
  pendingRemoval: true,
  blocker: null,
  lastRunId: 'old-run',
}
const settle = async () => {
  for (let i = 0; i < 5; i++)
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })
}
const draw = () =>
  app.root.render(
    <AppContext.Provider value={services}>
      <GitOpsPreviews source={source} active={active} healthy={true} onRun={onRun} />
    </AppContext.Provider>,
  )
const render = async () => {
  await act(async () => draw())
  await settle()
}
const control = (label: string) =>
  [...app.container.querySelectorAll('label')]
    .find((item) => item.querySelector('span')?.textContent === label)!
    .querySelector<HTMLInputElement | HTMLSelectElement>('input,select')!
const set = async (label: string, value: string) => {
  const item = control(label)
  await act(async () => {
    if (item instanceof HTMLSelectElement) {
      item.value = value
      item.dispatchEvent(new Event('change', { bubbles: true }))
    } else {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(item, value)
      item.dispatchEvent(new Event('input', { bubbles: true }))
    }
  })
  await settle()
}
const button = (label: string) =>
  [...app.container.querySelectorAll<HTMLButtonElement>('button')].find(
    (item) => item.textContent === label,
  )!
const click = async (label: string) => {
  await act(async () => button(label).click())
  await settle()
}
const selectResource = async () => {
  const checkbox = app.container.querySelector<HTMLInputElement>(
    'input[aria-label="Select RedisService/cache"]',
  )!
  await act(async () => checkbox.click())
  await settle()
}
function previewFor(request: PreviewRequest): GitOpsPreview {
  const base = previewFixture()
  const identity =
    request.intent === 'Removal' || request.intent === 'Adoption'
      ? request.resources[0]
      : request.intent === 'Replacement'
        ? request.replacements[0].original
        : { kind: 'RedisService', name: 'cache', resourceId: 'cache-id' }
  const action =
    request.intent === 'Adoption'
      ? 'Adopt'
      : request.intent === 'Replacement'
        ? 'Replace'
        : request.intent === 'Detach'
          ? 'Detach'
          : 'Remove'
  const warnings =
    action === 'Remove' || action === 'Replace' ? ['ProtectedData', 'DataMayBeLost'] : []
  return {
    ...base,
    intent: request.intent,
    commit: request.intent === 'Detach' ? null : base.commit,
    sourceRevision: source.revision,
    inventoryRevision: source.inventory.revision,
    input:
      request.intent === 'Detach'
        ? { kind: 'Inventory' }
        : {
            kind: 'Repository',
            repository:
              request.intent === 'Relocation'
                ? request.destination
                : base.input.kind === 'Repository'
                  ? base.input.repository
                  : source.repository,
            desiredHash: 'hash',
          },
    summary: {
      apply: 0,
      adopt: action === 'Adopt' ? 1 : 0,
      remove: action === 'Remove' ? 1 : 0,
      replace: action === 'Replace' ? 1 : 0,
      retain: 0,
      detach: action === 'Detach' ? 1 : 0,
      total: 1,
      warnings,
    },
    actions: {
      items: [
        {
          id: 'action',
          resource: identity,
          action,
          replacement:
            request.intent === 'Replacement'
              ? {
                  kind: identity.kind,
                  name: request.replacements[0].desired.name,
                  resourceId: 'server-reserved-new-id',
                }
              : null,
          dependencies: [],
          changedFields: [],
          warnings,
        },
      ],
      page: 1,
      pageSize: 100,
      hasMore: false,
      total: 1,
    },
  }
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
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
  app = createTestApp()
  services = gitOpsTestServices()
  services = {
    ...services,
    navigation: {
      ...services.navigation,
      search: '?view=gitops&resource=source&section=previews',
      setSearch: vi.fn((params) => {
        services = {
          ...services,
          navigation: { ...services.navigation, search: '?' + params.toString() },
        }
        draw()
      }),
    },
  }
  source = structuredClone(gitOpsSourceFixture)
  active = true
  metadata = null
  createResponse = null
  submitted.length = 0
  onRun.mockReset()
  requests.reset()
  requests.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/previews') && init?.method === 'POST') {
      const body = JSON.parse(init.body as string) as PreviewRequest
      submitted.push(body)
      metadata = previewFor(body)
      return createResponse ? createResponse(body) : jsonResponse(metadata, 201)
    }
    if (url.pathname.endsWith('/previews'))
      return jsonResponse({
        items: metadata ? [metadata] : [],
        page: 1,
        pageSize: 20,
        hasMore: false,
      })
    if (url.pathname.endsWith('/previews/preview')) return jsonResponse(metadata)
    if (url.pathname.endsWith('/resources'))
      return jsonResponse({
        items: [resource],
        page: 1,
        pageSize: 50,
        hasMore: false,
        inventoryRevision: source.inventory.revision,
      })
    if (url.pathname.endsWith('/adoption-candidates'))
      return jsonResponse({
        items: [
          {
            kind: 'ResourceGroup',
            name: 'manual',
            resourceId: 'manual-id',
            ownerSourceId: null,
            selectable: true,
            blocker: null,
            presence: 'Active',
            binding: null,
          },
        ],
        page: 1,
        pageSize: 50,
        hasMore: false,
      })
    if (url.pathname.endsWith('/git-provider-connections'))
      return jsonResponse([
        {
          id: 'connection',
          technicalName: 'github-demo',
          provider: 'GitHub',
          accountLogin: 'org',
          accountType: 'Organization',
          repositorySelection: 'All',
          createdAtUtc: 'now',
          updatedAtUtc: 'now',
        },
      ])
    if (url.pathname.endsWith('/repositories'))
      return jsonResponse({
        items: [
          {
            id: 'repo',
            name: 'repo',
            fullName: 'org/repo',
            htmlUrl: 'https://github.com/org/repo',
            isPrivate: true,
            defaultBranch: 'main',
          },
        ],
        page: 1,
        pageSize: 100,
        hasMore: false,
      })
    if (url.pathname.endsWith('/branches'))
      return jsonResponse({
        items: [{ name: 'main', commitSha: 'a'.repeat(40), isProtected: false }],
        page: 1,
        pageSize: 100,
        hasMore: false,
      })
    return requests.unhandled(input, init)
  })
  vi.stubGlobal('fetch', requests.fetch)
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
  requests.verify()
  vi.unstubAllGlobals()
})
it('builds a selected removal preview with only exact identities and opens the returned server plan', async () => {
  await render()
  await set('Lifecycle intent', 'Removal')
  await selectResource()
  await click('Build lifecycle preview')
  expect(submitted).toEqual([
    {
      intent: 'Removal',
      expectedRevision: 1,
      resources: [{ kind: 'RedisService', name: 'cache', resourceId: 'cache-id' }],
    },
  ])
  expect(app.container.textContent).toContain('Review Removal preview')
  expect(services.navigation.setSearch).toHaveBeenCalled()
  expect(services.navigation.search).toContain('selectionSource=source')
})
it('keeps the replacement draft across refresh but requires explicit review of a newer source revision', async () => {
  await render()
  await set('Lifecycle intent', 'Replacement')
  await selectResource()
  await set('Desired name for RedisService/cache', 'new-cache')
  source = { ...source, revision: 2 }
  await render()
  expect((control('Desired name for RedisService/cache') as HTMLInputElement).value).toBe(
    'new-cache',
  )
  expect(button('Build lifecycle preview').disabled).toBe(true)
  await click('Use reviewed source revision')
  await click('Build lifecycle preview')
  expect(submitted).toEqual([
    {
      intent: 'Replacement',
      expectedRevision: 2,
      replacements: [
        {
          original: { kind: 'RedisService', name: 'cache', resourceId: 'cache-id' },
          desired: { kind: 'RedisService', name: 'new-cache' },
        },
      ],
    },
  ])
  expect(app.container.textContent).toContain('server-reserved-new-id')
})
it('builds offline detach without requesting Git connections, repositories or branches', async () => {
  await render()
  await set('Lifecycle intent', 'Detach')
  await click('Build lifecycle preview')
  expect(submitted).toEqual([{ intent: 'Detach', expectedRevision: 1 }])
  expect(
    requests.fetch.mock.calls.some(([url]) => String(url).includes('git-provider-connections')),
  ).toBe(false)
  expect(app.container.textContent).toContain('Inventory only; repository access is not required.')
})
it('relocation submits only the new repository location and performs no source update', async () => {
  await render()
  await set('Lifecycle intent', 'Relocation')
  await set('Manifest directory', 'next/manifests')
  await click('Build lifecycle preview')
  expect(submitted).toEqual([
    {
      intent: 'Relocation',
      expectedRevision: 1,
      destination: {
        provider: 'GitHub',
        connectionId: 'connection',
        externalRepositoryId: 'repo',
        branch: 'main',
        manifestDirectory: 'next/manifests',
      },
    },
  ])
  expect(requests.fetch.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
})
it('an unknown preview creation outcome is inspect-only and never silently repeats POST', async () => {
  createResponse = () => Promise.reject(new TypeError('private transport details'))
  await render()
  await click('Build lifecycle preview')
  expect(button('Build lifecycle preview').disabled).toBe(true)
  expect(app.container.textContent).toContain('Inspect preview history')
  expect(app.container.textContent).not.toContain('private transport details')
  await click('Inspect preview history')
  expect(submitted).toHaveLength(1)
})
it('does not navigate from a held preview response after tenant change', async () => {
  const held = deferred<Response>()
  createResponse = () => held.promise
  await render()
  await click('Build lifecycle preview')
  services = {
    ...services,
    api: { ...services.api, tenantId: 'other' },
    tenant: { ...services.tenant, active: { ...services.tenant.active!, id: 'other' } },
  }
  await render()
  await act(async () => held.resolve(new Response(JSON.stringify(metadata), { status: 201 })))
  await settle()
  expect(services.navigation.setSearch).not.toHaveBeenCalled()
})
it('role loss clears adoption selectors and viewer controls stay read-only', async () => {
  await render()
  await set('Lifecycle intent', 'Adoption')
  await set('Existing technical name', 'manual')
  await click('Find adoption candidates')
  await click('Select candidate')
  expect(app.container.textContent).toContain('Selected identities (1)')
  services = { ...services, tenant: { ...services.tenant, canManage: false } }
  await render()
  expect((control('Lifecycle intent') as HTMLSelectElement).value).toBe('ResourceChanges')
  expect(button('Build lifecycle preview').disabled).toBe(true)
  services = { ...services, tenant: { ...services.tenant, canManage: true } }
  await render()
  await set('Lifecycle intent', 'Adoption')
  expect(app.container.textContent).toContain('Selected identities (0)')
})
it('retained removals and removed resources are distinct explicit requests', async () => {
  await render()
  await set('Declarations absent from the snapshot', 'Remove')
  await click('Build lifecycle preview')
  expect(submitted[0]).toEqual({
    intent: 'ResourceChanges',
    expectedRevision: 1,
    removal: 'Remove',
  })
})
