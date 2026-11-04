// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import type { AppServices } from '../../shell/contracts'
import { createTestApp } from '../../test/TestApp'
import { deferred, jsonResponse, strictFetch } from '../../test/http'
import { PreviewReview } from './PreviewReview'
import { largePreviewPage, previewFixture } from './previewFixtures'
import { gitOpsSourceFixture } from './testFixtures'
import type { GitOpsPreview, GitOpsSource } from './types'
const requests = strictFetch()
let app: ReturnType<typeof createTestApp>
let source: GitOpsSource
let services: AppServices
let active: boolean
let healthy: boolean
let metadata: GitOpsPreview
let pageTwo: ReturnType<typeof deferred<Response>> | null
let failRead: boolean
let confirm: (init?: RequestInit) => Promise<Response>
const onRun = vi.fn()
const onNew = vi.fn()
const render = async () => {
  await act(async () =>
    app.root.render(
      <AppContext.Provider value={services}>
        <PreviewReview
          source={source}
          previewId="preview"
          active={active}
          healthy={healthy}
          onRun={onRun}
          onNewPreview={onNew}
        />
      </AppContext.Provider>,
    ),
  )
  await settle()
}
const settle = async () => {
  for (let i = 0; i < 4; i++)
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })
}
const button = (label: string) =>
  [...app.container.querySelectorAll<HTMLButtonElement>('button')].find(
    (item) => item.textContent === label,
  )!
const click = async (label: string) => {
  await act(async () => button(label).click())
  await settle()
}
const text = () => app.container.textContent ?? ''
const acknowledge = async () => {
  const checkbox = app.container.querySelector<HTMLInputElement>('input[type=checkbox]')!
  await act(async () => checkbox.click())
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
  source = structuredClone(gitOpsSourceFixture)
  active = true
  healthy = true
  metadata = previewFixture()
  pageTwo = null
  failRead = false
  onRun.mockReset()
  onNew.mockReset()
  requests.reset()
  services = {
    auth: {
      initialized: true,
      authenticated: true,
      userId: 'user',
      isPlatformAdmin: false,
      login: vi.fn(),
      logout: vi.fn(),
    },
    tenant: {
      active: { id: 'tenant', name: 'Tenant', role: 'Owner', isDefault: true },
      canManage: true,
    },
    api: { baseUrl: 'https://api.test', tenantId: 'tenant', token: 'token' },
    navigation: {
      viewId: 'gitops',
      search: '?view=gitops&resource=source&selectionSource=source&preview=preview',
      setSearch: vi.fn(),
      goTo: vi.fn(),
      openResource: vi.fn(),
      resourceHref: vi.fn(),
      registerUnsavedChanges: () => () => {},
    },
    theme: { value: 'light', set: vi.fn() },
    notifications: { publish: vi.fn() },
    resourceTypes: [],
  }
  confirm = () => jsonResponse({ runId: 'accepted-run' }, 202)
  requests.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/previews/preview/confirm')) return confirm(init)
    if (url.pathname.endsWith('/previews/preview')) {
      if (failRead) return jsonResponse({ code: 'gitops.preview-not-found' }, 503)
      return url.searchParams.get('page') === '2'
        ? (pageTwo?.promise ?? jsonResponse(largePreviewPage(2)))
        : jsonResponse(metadata)
    }
    return requests.unhandled(input, init)
  })
  vi.stubGlobal('fetch', requests.fetch)
  app = createTestApp()
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
  requests.verify()
  vi.unstubAllGlobals()
})
it('waits for all pages and requires data-loss acknowledgment before sending exact confirmation', async () => {
  metadata = largePreviewPage(1)
  pageTwo = deferred<Response>()
  await render()
  expect(button('Review confirmation')).toBeUndefined()
  await act(async () => pageTwo!.resolve(new Response(JSON.stringify(largePreviewPage(2)))))
  await settle()
  expect(text()).toContain('101 plan actions')
  expect(text()).toContain('Stored data may be permanently lost.')
  await click('Review confirmation')
  expect(button('Confirm lifecycle plan').disabled).toBe(true)
  await acknowledge()
  expect(button('Confirm lifecycle plan').disabled).toBe(false)
  await click('Confirm lifecycle plan')
  expect(onRun).toHaveBeenCalledWith('accepted-run')
  const post = requests.fetch.mock.calls.find(([url]) => String(url).endsWith('/confirm'))!
  expect(JSON.parse(post[1]!.body as string)).toEqual({ expectedRevision: 1 })
})
it('never enables confirmation when any preview page fails', async () => {
  metadata = largePreviewPage(1)
  pageTwo = deferred<Response>()
  await render()
  await act(async () => pageTwo!.resolve(new Response('{}', { status: 503 })))
  await settle()
  expect(button('Review confirmation')).toBeUndefined()
  expect(text()).toContain('Complete preview unavailable.')
  expect(requests.fetch.mock.calls.some(([url]) => String(url).endsWith('/confirm'))).toBe(false)
})
it('closes approval when source or inventory revision changes', async () => {
  await render()
  await click('Review confirmation')
  await acknowledge()
  source = { ...source, inventory: { ...source.inventory, revision: 1 } }
  await render()
  expect(app.container.querySelector('dialog[open]')).toBeNull()
  expect(button('Review confirmation').disabled).toBe(true)
  expect(text()).toContain('Source or inventory changed.')
})
it('failed refresh clears the old acknowledgment and requires review again', async () => {
  await render()
  await click('Review confirmation')
  await acknowledge()
  failRead = true
  await click('Refresh complete preview')
  expect(app.container.querySelector('dialog[open]')).toBeNull()
  expect(button('Review confirmation').disabled).toBe(true)
  failRead = false
  await click('Refresh complete preview')
  await click('Review confirmation')
  expect(button('Confirm lifecycle plan').disabled).toBe(true)
})
it('keeps an accepted run when the follow-up GET fails without repeating the POST', async () => {
  confirm = () => {
    failRead = true
    return jsonResponse({ runId: 'accepted-run' }, 202)
  }
  await render()
  await click('Review confirmation')
  await acknowledge()
  await click('Confirm lifecycle plan')
  expect(onRun).toHaveBeenCalledTimes(1)
  expect(button('Open accepted run')).toBeDefined()
  await click('Refresh complete preview')
  expect(
    requests.fetch.mock.calls.filter(([url]) => String(url).endsWith('/confirm')),
  ).toHaveLength(1)
})
it('repeats unknown confirmation only after explicit action with the same preview revision', async () => {
  let attempts = 0
  confirm = () =>
    ++attempts === 1
      ? Promise.reject(new TypeError('private network details'))
      : jsonResponse({ runId: 'accepted-run' }, 202)
  await render()
  await click('Review confirmation')
  await acknowledge()
  await click('Confirm lifecycle plan')
  expect(text()).not.toContain('private network details')
  expect(attempts).toBe(1)
  await click('Repeat exact confirmation')
  const posts = requests.fetch.mock.calls.filter(([url]) => String(url).endsWith('/confirm'))
  expect(posts).toHaveLength(2)
  expect(posts[0][1]!.body).toBe(posts[1][1]!.body)
  expect(onRun).toHaveBeenCalledTimes(1)
})
it('stale confirmation requires a new preview and never retries automatically', async () => {
  confirm = () => jsonResponse({ code: 'gitops.preview-stale' }, 409)
  await render()
  await click('Review confirmation')
  await acknowledge()
  await click('Confirm lifecycle plan')
  expect(app.container.querySelector('dialog[open]')).toBeNull()
  expect(button('Review confirmation').disabled).toBe(true)
  expect(text()).toContain('Build and review a new preview.')
  expect(
    requests.fetch.mock.calls.filter(([url]) => String(url).endsWith('/confirm')),
  ).toHaveLength(1)
})
it('fences a held confirmation on tenant change', async () => {
  const held = deferred<Response>()
  confirm = () => held.promise
  await render()
  await click('Review confirmation')
  await acknowledge()
  await click('Confirm lifecycle plan')
  services = {
    ...services,
    api: { ...services.api, tenantId: 'other' },
    tenant: { active: { ...services.tenant.active!, id: 'other' }, canManage: true },
  }
  await render()
  await act(async () => held.resolve(new Response('{"runId":"old-tenant-run"}', { status: 202 })))
  await settle()
  expect(onRun).not.toHaveBeenCalled()
})
it('closes confirmation on role loss and renders viewer metadata without writes', async () => {
  await render()
  await click('Review confirmation')
  await acknowledge()
  services = { ...services, tenant: { ...services.tenant, canManage: false } }
  await render()
  expect(app.container.querySelector('dialog[open]')).toBeNull()
  expect(button('Review confirmation').disabled).toBe(true)
  expect(requests.fetch.mock.calls.some(([url]) => String(url).endsWith('/confirm'))).toBe(false)
})

it('a different preview revision resets acknowledgment and closes the old dialog', async () => {
  await render()
  await click('Review confirmation')
  await acknowledge()
  metadata = { ...metadata, revision: 2 }
  await click('Refresh complete preview')
  expect(app.container.querySelector('dialog[open]')).toBeNull()
  await click('Review confirmation')
  expect(button('Confirm lifecycle plan').disabled).toBe(true)
})
it('keeps the accepted Detach run when its own source revision and activity advance before refresh completes', async () => {
  metadata = { ...metadata, intent: 'Detach', input: { kind: 'Inventory' }, commit: null }
  const heldRead = deferred<Response>()
  let reads = 0
  requests.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/confirm')) return jsonResponse({ runId: 'detach-run' }, 202)
    if (url.pathname.endsWith('/previews/preview'))
      return ++reads === 1 ? jsonResponse(metadata) : heldRead.promise
    return requests.unhandled(input, init)
  })
  await render()
  await click('Review confirmation')
  await acknowledge()
  await click('Confirm lifecycle plan')
  expect(reads).toBe(2)
  source = { ...source, activity: 'Detaching', revision: 2 }
  await render()
  await act(async () =>
    heldRead.resolve(
      new Response(
        JSON.stringify({ ...metadata, state: 'Confirmed', revision: 2, runId: 'detach-run' }),
      ),
    ),
  )
  await settle()
  expect(onRun).toHaveBeenCalledTimes(1)
  expect(onRun).toHaveBeenCalledWith('detach-run')
})
it('closing the active screen fences a held confirmation callback', async () => {
  const held = deferred<Response>()
  confirm = () => held.promise
  await render()
  await click('Review confirmation')
  await acknowledge()
  await click('Confirm lifecycle plan')
  active = false
  await render()
  await act(async () => held.resolve(new Response('{"runId":"inactive-run"}', { status: 202 })))
  await settle()
  expect(onRun).not.toHaveBeenCalled()
  expect(app.container.querySelector('dialog[open]')).toBeNull()
})
