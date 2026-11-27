// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import { createTestApp } from '../../test/TestApp'
import { strictFetch, jsonResponse, deferred } from '../../test/http'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { gitOpsTestServices } from './testServices'
import { gitOpsSourceFixture } from './testFixtures'
import { NewSource } from './NewSource'
import { SourceSettings } from './SourceSettings'
import { SourceCommands } from './SourceCommands'
import { fetchSource } from './api'
import { useGitOpsQuery } from './useGitOpsQuery'
import type { GitOpsSource } from './types'
let app: ReturnType<typeof createTestApp>
let services = gitOpsTestServices()
let source: GitOpsSource
const requests = strictFetch()
const actions = {
  open: vi.fn(),
  created: vi.fn(),
  loaded: vi.fn(),
  dirty: vi.fn(),
  closed: vi.fn(),
}
const accepted = vi.fn()
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
  services = gitOpsTestServices()
  source = structuredClone(gitOpsSourceFixture)
  app = createTestApp()
  requests.reset()
  vi.clearAllMocks()
  vi.stubGlobal('fetch', requests.fetch)
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
  requests.verify()
  vi.unstubAllGlobals()
})
const render = (node: React.ReactNode) =>
  act(async () =>
    app.root.render(<AppContext.Provider value={services}>{node}</AppContext.Provider>),
  )
const button = (text: string) =>
  [...app.container.querySelectorAll<HTMLButtonElement>('button')].find(
    (item) => item.textContent === text,
  )!
const field = (label: string) =>
  [...app.container.querySelectorAll('label')]
    .find((item) => item.querySelector('.field__label')?.textContent === label)!
    .querySelector<HTMLInputElement | HTMLSelectElement>('input,select')!
async function change(label: string, value: string) {
  await act(async () => {
    const node = field(label)
    const proto =
      node.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype
    Object.getOwnPropertyDescriptor(proto, 'value')!.set!.call(node, value)
    node.dispatchEvent(new Event(node.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }))
  })
}
async function submit() {
  await act(async () =>
    app.container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
}
function pickerReads(path: string) {
  if (path.endsWith('/git-provider-connections'))
    return jsonResponse([{ id: 'connection', technicalName: 'github', accountLogin: 'org' }])
  if (path.endsWith('/repositories'))
    return jsonResponse({
      items: [{ id: 'repo', fullName: 'org/repo', defaultBranch: 'main' }],
      page: 1,
      pageSize: 100,
      hasMore: false,
    })
  if (path.endsWith('/branches'))
    return jsonResponse({ items: [{ name: 'main' }], page: 1, pageSize: 100, hasMore: false })
  return null
}
it('creates through existing provider pickers without analyzer or SourceRepository writes', async () => {
  const bodies: unknown[] = []
  requests.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/gitops/sources/') && init?.method === 'POST') {
      bodies.push(JSON.parse(init.body as string))
      return jsonResponse(source)
    }
    return pickerReads(url.pathname) ?? requests.unhandled(input, init)
  })
  await render(<NewSource active actions={actions} />)
  await change('GitHub connection', 'connection')
  await change('Manifest repository', 'repo')
  expect(field('Polling interval (seconds)').value).toBe('300')
  await submit()
  expect(bodies).toEqual([
    {
      provider: 'GitHub',
      connectionId: 'connection',
      externalRepositoryId: 'repo',
      branch: 'main',
      manifestDirectory: '',
      pollIntervalSeconds: 300,
      prune: 'Retain',
      idempotencyKey: expect.any(String),
    },
  ])
  expect(actions.created).toHaveBeenCalledWith('source', expect.any(String))
})
it('repeats an unknown creation only with its original input and key', async () => {
  const bodies: unknown[] = []
  let fail = true
  requests.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/gitops/sources/') && init?.method === 'POST') {
      bodies.push(JSON.parse(init.body as string))
      return fail ? Promise.reject(new TypeError('Connection lost')) : jsonResponse(source)
    }
    return pickerReads(url.pathname) ?? requests.unhandled(input, init)
  })
  await render(<NewSource active actions={actions} />)
  await change('GitHub connection', 'connection')
  await change('Manifest repository', 'repo')
  await submit()
  expect(button('Repeat same creation request')).toBeDefined()
  fail = false
  await submit()
  expect(bodies).toHaveLength(2)
  expect(bodies[1]).toEqual(bodies[0])
  expect(actions.created).toHaveBeenCalledTimes(1)
})
it('preserves a dirty settings draft and explicitly rebases only edited fields', async () => {
  const bodies: unknown[] = []
  requests.fetch.mockImplementation((input, init) => {
    if (init?.method === 'PUT') {
      bodies.push(JSON.parse(init.body as string))
      return jsonResponse({ ...source, revision: 3, pollIntervalSeconds: 600, prune: 'Prune' })
    }
    return requests.unhandled(input, init)
  })
  await render(<SourceSettings source={source} active healthy actions={actions} />)
  await change('Polling interval (seconds)', '600')
  source = { ...source, revision: 2, pollIntervalSeconds: 420, prune: 'Prune' }
  await render(<SourceSettings source={source} active healthy actions={actions} />)
  expect(field('Polling interval (seconds)').value).toBe('600')
  expect(button('Save settings').disabled).toBe(true)
  await act(async () => button('Review and reapply').click())
  expect(field('Prune policy').value).toBe('Prune')
  await submit()
  expect(bodies).toEqual([{ expectedRevision: 2, pollIntervalSeconds: 600, prune: 'Prune' }])
})
it('clears a confirmation and dirty management settings on permission loss', async () => {
  await render(
    <>
      <SourceCommands source={source} active healthy onAccepted={accepted} onDeleted={vi.fn()} />
      <SourceSettings source={source} active healthy actions={actions} />
    </>,
  )
  await act(async () => button('Pause').click())
  expect(app.container.querySelector('dialog')).not.toBeNull()
  await change('Polling interval (seconds)', '600')
  services = { ...services, tenant: { ...services.tenant, canManage: false } }
  await render(
    <>
      <SourceCommands source={source} active healthy onAccepted={accepted} onDeleted={vi.fn()} />
      <SourceSettings source={source} active healthy actions={actions} />
    </>,
  )
  expect(app.container.querySelector('dialog')).toBeNull()
  expect(field('Polling interval (seconds)').value).toBe('300')
  expect(button('Save settings').disabled).toBe(true)
  expect(requests.fetch).not.toHaveBeenCalled()
})
function CommandsProbe() {
  const query = useGitOpsQuery(['source', 'source'], true, false, (api, signal) =>
    fetchSource(api, 'source', signal),
  )
  return (
    <>
      {query.error && <p>{query.error}</p>}
      {query.resource && (
        <SourceCommands
          source={query.resource}
          active
          healthy={hasHealthyCurrentQueryData(query)}
          onAccepted={accepted}
          onDeleted={vi.fn()}
        />
      )}
    </>
  )
}
it('retains accepted Pause when the invalidation read fails, with no second write', async () => {
  let reads = 0
  let writes = 0
  requests.fetch.mockImplementation((input, init) => {
    const path = new URL(String(input)).pathname
    if (path.endsWith('/pause')) {
      writes++
      return jsonResponse({ ...source, revision: 2, activity: 'Paused' })
    }
    if (path.endsWith('/sources/source')) {
      reads++
      return reads === 1 ? jsonResponse(source) : jsonResponse({ title: 'Unavailable' }, 503)
    }
    return requests.unhandled(input, init)
  })
  await render(<CommandsProbe />)
  await act(async () => button('Pause').click())
  await act(async () => button('Confirm pause').click())
  expect(writes).toBe(1)
  expect(accepted).toHaveBeenCalledTimes(1)
  expect(app.container.textContent).toContain('pause accepted')
  expect(button('Pause').disabled).toBe(true)
})
it('retains accepted Pause across its own activity/revision transition', async () => {
  let paused = false
  requests.fetch.mockImplementation((input, init) => {
    const path = new URL(String(input)).pathname
    if (path.endsWith('/pause')) {
      paused = true
      return jsonResponse({ ...source, revision: 2, activity: 'Paused' })
    }
    if (path.endsWith('/sources/source'))
      return jsonResponse(paused ? { ...source, revision: 2, activity: 'Paused' } : source)
    return requests.unhandled(input, init)
  })
  await render(<CommandsProbe />)
  await act(async () => button('Pause').click())
  await act(async () => button('Confirm pause').click())
  expect(accepted).toHaveBeenCalledTimes(1)
  expect(button('Resume')).toBeDefined()
  expect(app.container.querySelector('dialog')).toBeNull()
})
it('fences a held command after the originating tenant exits', async () => {
  const pending = deferred<Response>()
  let signal: AbortSignal | undefined
  requests.fetch.mockImplementation((_input, init) => {
    signal = init?.signal as AbortSignal
    return pending.promise
  })
  await render(
    <SourceCommands source={source} active healthy onAccepted={accepted} onDeleted={vi.fn()} />,
  )
  await act(async () => button('Pause').click())
  await act(async () => button('Confirm pause').click())
  services = { ...services, api: { ...services.api, tenantId: 'other' } }
  await render(
    <SourceCommands source={source} active healthy onAccepted={accepted} onDeleted={vi.fn()} />,
  )
  expect(signal?.aborted).toBe(true)
  expect(app.container.querySelector('dialog')).toBeNull()
  await act(async () => pending.resolve(new Response(JSON.stringify(source))))
  expect(accepted).not.toHaveBeenCalled()
})

it('closes a captured source command when the selected identity changes at the same revision', async () => {
  await render(
    <SourceCommands source={source} active healthy onAccepted={accepted} onDeleted={vi.fn()} />,
  )
  await act(async () => button('Pause').click())
  expect(app.container.querySelector('dialog')).not.toBeNull()
  source = { ...source, id: 'another-source' }
  await render(
    <SourceCommands source={source} active healthy onAccepted={accepted} onDeleted={vi.fn()} />,
  )
  expect(app.container.querySelector('dialog')).toBeNull()
  expect(requests.fetch).not.toHaveBeenCalled()
})
it('does not reapply one source settings draft to a different identity', async () => {
  await render(<SourceSettings source={source} active healthy actions={actions} />)
  await change('Polling interval (seconds)', '600')
  source = { ...source, id: 'another-source', pollIntervalSeconds: 420 }
  await render(<SourceSettings source={source} active healthy actions={actions} />)
  expect(field('Polling interval (seconds)').value).toBe('420')
  expect(button('Save settings').disabled).toBe(true)
})
