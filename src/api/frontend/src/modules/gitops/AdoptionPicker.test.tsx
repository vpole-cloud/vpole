// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import { createTestApp } from '../../test/TestApp'
import { deferred, jsonResponse, strictFetch } from '../../test/http'
import { AdoptionPicker } from './AdoptionPicker'
import { gitOpsTestServices } from './testServices'
import type { AdoptionCandidate, ResourceIdentity } from './types'
const requests = strictFetch()
let app: ReturnType<typeof createTestApp>
let services: ReturnType<typeof gitOpsTestServices>
let selected: ResourceIdentity[]
let candidates: AdoptionCandidate[]
let disabled: boolean
let active: boolean
const onChange = vi.fn()
const candidate = (name: string): AdoptionCandidate => ({
  kind: 'ResourceGroup',
  name,
  resourceId: name + '-id',
  ownerSourceId: null,
  selectable: true,
  blocker: null,
  presence: 'Active',
  binding: null,
})
const settle = async () => {
  for (let i = 0; i < 4; i++)
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0))
    })
}
const render = async () => {
  await act(async () =>
    app.root.render(
      <AppContext.Provider value={services}>
        <AdoptionPicker
          sourceId="source"
          active={active}
          disabled={disabled}
          selected={selected}
          onChange={onChange}
        />
      </AppContext.Provider>,
    ),
  )
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
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  app = createTestApp()
  services = gitOpsTestServices()
  selected = []
  candidates = [candidate('manual')]
  disabled = false
  active = true
  onChange.mockReset()
  requests.reset()
  requests.fetch.mockImplementation((input, init) =>
    String(input).includes('/adoption-candidates?')
      ? jsonResponse({ items: candidates, page: 1, pageSize: 50, hasMore: false })
      : requests.unhandled(input, init),
  )
  vi.stubGlobal('fetch', requests.fetch)
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
  requests.verify()
  vi.unstubAllGlobals()
})
it('looks up an exact named identity with current tenant/auth and returns only selectors', async () => {
  await render()
  await set('Existing technical name', 'manual')
  await click('Find adoption candidates')
  await click('Select candidate')
  expect(onChange).toHaveBeenCalledWith([
    { kind: 'ResourceGroup', name: 'manual', resourceId: 'manual-id' },
  ])
  const [url, init] = requests.fetch.mock.calls[0]
  expect(String(url)).toContain('kind=ResourceGroup&name=manual')
  expect(init!.headers).toMatchObject({
    Authorization: 'Bearer token',
    'X-Pole-Tenant-ID': 'tenant',
  })
})
it('finds unnamed bindings through Secret and consumer and keeps declaration name separate', async () => {
  candidates = [
    {
      kind: 'SecretBinding',
      name: null,
      resourceId: 'binding',
      ownerSourceId: null,
      selectable: true,
      blocker: null,
      presence: 'Active',
      binding: {
        secret: { name: 'auth', resourceId: 'secret' },
        consumer: { kind: 'WebService', name: 'app', resourceId: 'app-id' },
        usage: 'Runtime',
        targetScope: { mode: 'All', deploymentIds: [] },
      },
    },
  ]
  await render()
  await set('Resource kind', 'SecretBinding')
  await set('Secret technical name', 'auth')
  await set('Consumer technical name', 'app')
  await click('Find adoption candidates')
  expect(button('Select candidate').disabled).toBe(true)
  await set('Binding declaration name in Git', 'app-auth')
  await click('Select candidate')
  expect(onChange).toHaveBeenCalledWith([
    { kind: 'SecretBinding', name: 'app-auth', resourceId: 'binding' },
  ])
  const query = new URL(String(requests.fetch.mock.calls[0][0])).searchParams
  expect(Object.fromEntries(query)).toMatchObject({
    kind: 'SecretBinding',
    relatedKind: 'Secret',
    relatedName: 'auth',
    consumerKind: 'WebService',
    consumerName: 'app',
  })
  expect(query.has('name')).toBe(false)
})
it('does not select foreign Git ownership or a deleting candidate', async () => {
  candidates = [
    {
      ...candidate('owned'),
      ownerSourceId: 'another-source',
      selectable: false,
      blocker: 'GitDeclarationOwnershipConflict',
    },
  ]
  await render()
  await set('Existing technical name', 'owned')
  await click('Find adoption candidates')
  expect(button('Select candidate').disabled).toBe(true)
  expect(onChange).not.toHaveBeenCalled()
})
it('requires searching again after the visible name changes', async () => {
  await render()
  await set('Existing technical name', 'manual')
  await click('Find adoption candidates')
  await set('Existing technical name', 'different')
  expect(button('Select candidate').disabled).toBe(true)
  expect(app.container.textContent).toContain('Search fields changed.')
})
it('does not replace a newer search with a held old response', async () => {
  const held = deferred<Response>()
  requests.fetch.mockImplementation((input, init) => {
    if (!String(input).includes('/adoption-candidates?')) return requests.unhandled(input, init)
    return new URL(String(input)).searchParams.get('name') === 'old'
      ? held.promise
      : jsonResponse({ items: [candidate('new')], page: 1, pageSize: 50, hasMore: false })
  })
  await render()
  await set('Existing technical name', 'old')
  await click('Find adoption candidates')
  await set('Existing technical name', 'new')
  await click('Find adoption candidates')
  await act(async () =>
    held.resolve(
      new Response(
        JSON.stringify({ items: [candidate('old')], page: 1, pageSize: 50, hasMore: false }),
      ),
    ),
  )
  await settle()
  expect(app.container.textContent).toContain('ResourceGroup/new')
  expect(app.container.textContent).not.toContain('ResourceGroup/old')
})
it('disables selectors when permission or active-screen ownership departs', async () => {
  await render()
  await set('Existing technical name', 'manual')
  await click('Find adoption candidates')
  disabled = true
  active = false
  await render()
  expect(button('Select candidate')).toBeUndefined()
  expect(button('Find adoption candidates').disabled).toBe(true)
})
