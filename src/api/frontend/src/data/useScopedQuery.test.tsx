// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import type { Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it } from 'vitest'
import { useScopedQuery } from './useScopedQuery'
import { createTestApp } from '../test/TestApp'
import { deferred } from '../test/http'

let root: Root
beforeEach(() => { Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true }); root = createTestApp().root })
afterEach(async () => { await act(async () => root.unmount()) })

it('coalesces repeated refreshes of a slow query and keeps the result visible on the next failure', async () => {
  const pending = deferred<string>()
  let calls = 0
  let fail = false
  let signal!: AbortSignal
  let state!: ReturnType<typeof useScopedQuery<string>>
  function Probe() {
    const value = useScopedQuery({ scope: ['api', 'tenant'], key: ['detail'], load: async (readSignal) => {
      signal = readSignal; calls++
      if (fail) throw new Error('Read unavailable')
      return pending.promise
    } })
    useLayoutEffect(() => { state = value })
    return null
  }
  await act(async () => root.render(<Probe />))
  await act(async () => { void state.refresh(); void state.refresh(); void state.refresh() })
  expect(calls).toBe(1)
  expect(signal.aborted).toBe(false)
  await act(async () => pending.resolve('Applied'))
  expect(state.resource).toBe('Applied')
  fail = true
  let refreshed: string | null = 'unset'
  await act(async () => { refreshed = await state.refresh() })
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
    const value = useScopedQuery({ scope: ['api', tenant], key: ['detail'], keepPrevious: true,
      load: async (readSignal) => { if (tenant === 'alpha') { signal = readSignal; return pending.promise } return 'Beta' } })
    useLayoutEffect(() => { state = value })
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
    const value = useScopedQuery({ scope: ['api', tenant], key: ['detail'], load: async () => { calls++; return tenant } })
    useLayoutEffect(() => { state = value })
    return null
  }
  await act(async () => root.render(<Probe tenant="alpha" />))
  const oldRefresh = state.refresh
  await act(async () => root.render(<Probe tenant="beta" />))
  const before = calls
  await act(async () => { expect(await oldRefresh()).toBeNull() })
  expect(calls).toBe(before)
  const closingRefresh = state.refresh
  await act(async () => root.render(null))
  await act(async () => { expect(await closingRefresh()).toBeNull() })
  expect(calls).toBe(before)
})
