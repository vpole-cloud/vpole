// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import { createTestApp } from '../../test/TestApp'
import { deferred } from '../../test/http'
import { gitOpsTestServices } from './testServices'
import { useGitOpsQuery } from './useGitOpsQuery'
let app: ReturnType<typeof createTestApp>
let hidden = false
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers()
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  hidden = false
  app = createTestApp()
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
  vi.useRealTimers()
})
it('polls only the active visible screen and follows5/20s managed metadata', async () => {
  let calls = 0
  let running = true
  function Probe({ active }: { active: boolean }) {
    useGitOpsQuery<{ running: boolean }>(
      ['probe'],
      active,
      (value) => (value?.running ? 5000 : 20000),
      async () => {
        calls++
        return { running }
      },
    )
    return null
  }
  const services = gitOpsTestServices()
  const render = (active: boolean) =>
    act(async () =>
      app.root.render(
        <AppContext.Provider value={services}>
          <Probe active={active} />
        </AppContext.Provider>,
      ),
    )
  await render(false)
  expect(calls).toBe(0)
  await render(true)
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1)
  })
  expect(calls).toBe(1)
  await act(async () => {
    await vi.advanceTimersByTimeAsync(5000)
  })
  expect(calls).toBe(2)
  running = false
  await act(async () => {
    await vi.advanceTimersByTimeAsync(5000)
  })
  expect(calls).toBe(3)
  await act(async () => {
    await vi.advanceTimersByTimeAsync(10000)
  })
  expect(calls).toBe(3)
  await render(false)
  await act(async () => {
    await vi.advanceTimersByTimeAsync(30000)
  })
  expect(calls).toBe(3)
  await render(true)
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1)
  })
  const shown = calls
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await act(async () => {
    await vi.advanceTimersByTimeAsync(40000)
  })
  expect(calls).toBe(shown)
})
it('coalesces slow polling instead of canceling the in-flight read on each tick', async () => {
  const pending = deferred<{ running: boolean }>()
  let calls = 0
  let signal: AbortSignal | undefined
  let state: ReturnType<typeof useGitOpsQuery<{ running: boolean }>> | undefined
  function Probe() {
    const query = useGitOpsQuery<{ running: boolean }>(
      ['slow'],
      true,
      5000,
      async (_api, current) => {
        calls++
        signal = current
        return calls === 1 ? { running: true } : pending.promise
      },
    )
    useLayoutEffect(() => {
      state = query
    })
    return null
  }
  await act(async () =>
    app.root.render(
      <AppContext.Provider value={gitOpsTestServices()}>
        <Probe />
      </AppContext.Provider>,
    ),
  )
  await act(async () => {
    await vi.advanceTimersByTimeAsync(5001)
  })
  expect(calls).toBe(2)
  await act(async () => {
    await vi.advanceTimersByTimeAsync(15000)
  })
  expect(calls).toBe(2)
  expect(signal?.aborted).toBe(false)
  await act(async () => pending.resolve({ running: false }))
  expect(state?.resource).toEqual({ running: false })
})
it('fences a previous tenant response and obtains credentials at request time', async () => {
  const pending = deferred<string>()
  let oldSignal: AbortSignal | undefined
  const tokens: (string | undefined)[] = []
  let state: ReturnType<typeof useGitOpsQuery<string>> | undefined
  function Probe() {
    const query = useGitOpsQuery(['scope'], true, false, async (api, signal) => {
      tokens.push(api.credential())
      if (api.tenantId === 'tenant') {
        oldSignal = signal
        return pending.promise
      }
      return 'beta'
    })
    useLayoutEffect(() => {
      state = query
    })
    return null
  }
  const render = (tenantId: string, token: string) =>
    act(async () =>
      app.root.render(
        <AppContext.Provider
          value={gitOpsTestServices({ api: { baseUrl: 'https://api.test', tenantId, token } })}
        >
          <Probe />
        </AppContext.Provider>,
      ),
    )
  await render('tenant', 'first')
  await render('beta', 'renewed')
  expect(oldSignal?.aborted).toBe(true)
  await act(async () => pending.resolve('late alpha'))
  expect(state?.resource).toBe('beta')
  expect(tokens).toEqual(['first', 'renewed'])
})
