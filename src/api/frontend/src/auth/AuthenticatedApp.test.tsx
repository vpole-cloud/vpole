// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'

const { client } = vi.hoisted(() => ({
  client: {
    init: vi.fn<(...args: unknown[]) => Promise<boolean>>(),
    authenticated: true,
    token: 'test-access-token',
    idToken: 'test-id-token',
    refreshToken: 'test-refresh-token',
    onReady: undefined as ((authenticated: boolean) => void) | undefined,
    onAuthSuccess: undefined as (() => void) | undefined,
    onAuthRefreshSuccess: undefined as (() => void) | undefined,
  },
}))
vi.mock('./keycloak', () => ({ default: client }))
vi.mock('../App', () => ({
  default: () => <input aria-label="Workspace draft" defaultValue="saved" />,
}))
import { AuthenticatedApp } from './AuthenticatedApp'

let root: Root
let container: HTMLDivElement
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  window.history.replaceState(null, '', '/')
  client.init.mockReset().mockImplementation(async () => {
    client.onReady?.(true)
    client.onAuthSuccess?.()
    return true
  })
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
})
it('keeps identity initialization and workspace drafts stable across navigation and token renewal', async () => {
  await act(async () => root.render(<AuthenticatedApp />))
  expect(client.init).toHaveBeenCalledTimes(1)
  expect(client.init.mock.calls[0][0]).not.toHaveProperty('redirectUri')
  const draft = container.querySelector('input')!
  draft.value = 'unsaved configuration'
  window.history.replaceState(null, '', '/?view=web-services&tenant=alpha&resource=service')
  await act(async () => client.onAuthRefreshSuccess?.())
  expect(client.init).toHaveBeenCalledTimes(1)
  expect(container.querySelector('input')).toBe(draft)
  expect(container.querySelector('input')?.value).toBe('unsaved configuration')
})
