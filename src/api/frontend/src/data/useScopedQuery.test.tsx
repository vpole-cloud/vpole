// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { fetchScopedQuery } from './fetchScopedQuery'
import { useApiScope } from './useApiScope'
import { act, useLayoutEffect } from 'react'
import type { Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it } from 'vitest'
import { useScopedQuery } from './useScopedQuery'
import { createTestApp } from '../test/TestApp'
import { deferred } from '../test/http'

let root: Root
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  root = createTestApp().root
})
afterEach(async () => {
  await act(async () => root.unmount())
})

it('coalesces repeated refreshes of a slow query and keeps the result visible on the next failure', async () => {
  const pending = deferred<string>()
  let calls = 0
  let fail = false
  let signal!: AbortSignal
  let state!: ReturnType<typeof useScopedQuery<string>>
  function Probe() {
    const value = useScopedQuery({
      scope: ['api', 'tenant'],
      key: ['detail'],
      load: async (readSignal) => {
        signal = readSignal
        calls++
        if (fail) throw new Error('Read unavailable')
        return pending.promise
      },
    })
    useLayoutEffect(() => {
      state = value
    })
    return null
  }
  await act(async () => root.render(<Probe />))
  await act(async () => {
    void state.refresh()
    void state.refresh()
    void state.refresh()
  })
  expect(calls).toBe(1)
  expect(signal.aborted).toBe(false)
  await act(async () => pending.resolve('Applied'))
  expect(state.resource).toBe('Applied')
  fail = true
  let refreshed: string | null = 'unset'
  await act(async () => {
    refreshed = await state.refresh()
  })
  expect(refreshed).toBeNull()
  expect(state.resource).toBe('Applied')
  expect(state.error).toBe('Read unavailable')
  expect(state.status).toBe('error')
})

it('never presents a previous tenant as placeholder data and cancels its pending read', async () => {
  const pending = deferred<string>()
  let signal!: AbortSignal
  let state!: ReturnType<typeof useScopedQuery<string>>
  function Probe({ tenant }: { tenant: string }) {
    const value = useScopedQuery({
      scope: ['api', tenant],
      key: ['detail'],
      keepPrevious: true,
      load: async (readSignal) => {
        if (tenant === 'alpha') {
          signal = readSignal
          return pending.promise
        }
        return 'Beta'
      },
    })
    useLayoutEffect(() => {
      state = value
    })
    return null
  }
  await act(async () => root.render(<Probe tenant="alpha" />))
  await act(async () => root.render(<Probe tenant="beta" />))
  expect(signal.aborted).toBe(true)
  expect(state.resource).toBe('Beta')
  await act(async () => pending.resolve('Late Alpha'))
  expect(state.resource).toBe('Beta')
})

it('rejects an old refresh callback after tenant change and after unmount', async () => {
  let calls = 0
  let state!: ReturnType<typeof useScopedQuery<string>>
  function Probe({ tenant }: { tenant: string }) {
    const value = useScopedQuery({
      scope: ['api', tenant],
      key: ['detail'],
      load: async () => {
        calls++
        return tenant
      },
    })
    useLayoutEffect(() => {
      state = value
    })
    return null
  }
  await act(async () => root.render(<Probe tenant="alpha" />))
  const oldRefresh = state.refresh
  await act(async () => root.render(<Probe tenant="beta" />))
  const before = calls
  await act(async () => {
    expect(await oldRefresh()).toBeNull()
  })
  expect(calls).toBe(before)
  const closingRefresh = state.refresh
  await act(async () => root.render(null))
  await act(async () => {
    expect(await closingRefresh()).toBeNull()
  })
  expect(calls).toBe(before)
})

it('cancels a disabled in-flight read only after its last enabled observer leaves', async () => {
  const pending = deferred<string>()
  let signal!: AbortSignal
  let calls = 0
  const states: Record<string, ReturnType<typeof useScopedQuery<string>>> = {}
  function Probe({ id, enabled }: { id: string; enabled: boolean }) {
    const value = useScopedQuery({
      scope: ['api', 'tenant'],
      key: ['shared'],
      enabled,
      load: async (readSignal) => {
        calls++
        signal = readSignal
        return pending.promise
      },
    })
    useLayoutEffect(() => {
      states[id] = value
    })
    return null
  }
  const render = (a: boolean, b: boolean) =>
    root.render(
      <>
        <Probe id="a" enabled={a} />
        <Probe id="b" enabled={b} />
      </>,
    )
  await act(async () => render(true, true))
  expect(calls).toBe(1)
  await act(async () => render(false, true))
  expect(signal.aborted).toBe(false)
  await act(async () => render(false, false))
  expect(signal.aborted).toBe(true)
  await act(async () => pending.resolve('Late response'))
  expect(states.a.resource).toBeNull()
  expect(states.b.resource).toBeNull()
})

it.each(['declarative', 'imperative'])(
  'shares one pending read and cache value when %s starts first',
  async (first) => {
    const pending = deferred<string[]>()
    let calls = 0
    let origin!: ReturnType<typeof useApiScope>
    let state!: ReturnType<typeof useScopedQuery<string[]>>
    const parts = ['eligibility', 'deployment-targets', 'pole/web-service', null, false]
    const load = async () => {
      calls++
      return pending.promise
    }
    function Probe({ observe }: { observe: boolean }) {
      const scope = useApiScope({ baseUrl: 'api', tenantId: 'tenant' })
      const query = useScopedQuery({ scope: ['api', 'tenant'], key: parts, enabled: observe, load })
      useLayoutEffect(() => {
        origin = scope
        state = query
      })
      return null
    }
    await act(async () => root.render(<Probe observe={first === 'declarative'} />))
    let read!: Promise<string[]>
    await act(async () => {
      read = fetchScopedQuery(origin, parts, load)
    })
    if (first === 'imperative') await act(async () => root.render(<Probe observe />))
    expect(calls).toBe(1)
    await act(async () => {
      pending.resolve(['target'])
      await read
    })
    expect(await read).toEqual(['target'])
    expect(state.resource).toEqual(['target'])
  },
)

it('keeps a shared imperative read alive when its rendered observer disables', async () => {
  const pending = deferred<string[]>()
  const parts = ['eligibility', 'shared-read']
  let origin!: ReturnType<typeof useApiScope>
  let signal!: AbortSignal
  let calls = 0
  const load = async (readSignal: AbortSignal) => {
    signal = readSignal
    calls++
    return pending.promise
  }
  function Probe({ enabled }: { enabled: boolean }) {
    const scope = useApiScope({ baseUrl: 'api', tenantId: 'tenant' })
    useScopedQuery({ scope: ['api', 'tenant'], key: parts, enabled, load })
    useLayoutEffect(() => {
      origin = scope
    })
    return null
  }
  await act(async () => root.render(<Probe enabled />))
  let result!: Promise<string[] | null>
  await act(async () => {
    result = fetchScopedQuery(origin, parts, load).catch(() => null)
  })
  await act(async () => root.render(<Probe enabled={false} />))
  expect(signal.aborted).toBe(false)
  expect(calls).toBe(1)
  await act(async () => {
    pending.resolve(['target'])
    await result
  })
  expect(await result).toEqual(['target'])
})

it.each([false, true])(
  'releases an exiting imperative owner while the rendered observer is enabled=%s',
  async (enabled) => {
    const pending = deferred<string[]>()
    const parts = ['eligibility', 'owner-exit']
    let origin!: ReturnType<typeof useApiScope>
    let signal!: AbortSignal
    let state!: ReturnType<typeof useScopedQuery<string[]>>
    const load = async (readSignal: AbortSignal) => {
      signal = readSignal
      return pending.promise
    }
    function Caller() {
      const scope = useApiScope({ baseUrl: 'api', tenantId: 'tenant' })
      useLayoutEffect(() => {
        origin = scope
      })
      return null
    }
    function Observer() {
      const query = useScopedQuery({ scope: ['api', 'tenant'], key: parts, enabled, load })
      useLayoutEffect(() => {
        state = query
      })
      return null
    }
    const render = (caller: boolean) =>
      root.render(
        <>
          {caller && <Caller />}
          <Observer />
        </>,
      )
    await act(async () => render(true))
    let result!: Promise<string[] | null>
    await act(async () => {
      result = fetchScopedQuery(origin, parts, load).catch(() => null)
    })
    await act(async () => render(false))
    expect(signal.aborted).toBe(!enabled)
    await act(async () => {
      pending.resolve(['target'])
      await result
    })
    expect(await result).toBeNull()
    expect(state.resource).toEqual(enabled ? ['target'] : null)
    expect(
      origin.session.client
        .getQueryCache()
        .find({ queryKey: origin.key(parts), exact: true })
        ?.getObserversCount(),
    ).toBe(1)
  },
)
