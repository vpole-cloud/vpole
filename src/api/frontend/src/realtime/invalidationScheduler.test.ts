// @vitest-environment jsdom
import { QueryObserver } from '@tanstack/react-query'
import { afterEach, expect, it, vi } from 'vitest'
import { createQueryClient, scopedQueryKey, type QuerySession } from '../data/querySession'
import { deferred } from '../test/http'
import { InvalidationScheduler } from './invalidationScheduler'

afterEach(() => vi.useRealTimers())
const session = (): QuerySession => ({ identity: 'account', id: 'session', client: createQueryClient(), isActive: () => true })
const settle = async () => { for (let index = 0; index < 8; index++) await Promise.resolve() }

it('coalesces a burst, keeps a slow GET alive, and performs exactly one trailing GET for changes during it', async () => {
  vi.useFakeTimers()
  const owner = session(); const first = deferred<string>(); const trailing = deferred<string>()
  const queryKey = scopedQueryKey(owner, ['api', 'tenant'], ['source-builds', 'jobs', 'service'])
  let signal!: AbortSignal
  const load = vi.fn(({ signal: value }: { signal: AbortSignal }) => { signal = value; return load.mock.calls.length === 1 ? first.promise : trailing.promise })
  const observer = new QueryObserver(owner.client, { queryKey, queryFn: load })
  const release = observer.subscribe(() => {})
  const scheduler = new InvalidationScheduler(owner, () => true)
  for (let index = 0; index < 20; index++) scheduler.invalidate(() => true)
  await vi.advanceTimersByTimeAsync(250)
  expect(load).toHaveBeenCalledTimes(1)
  expect(signal.aborted).toBe(false)
  scheduler.invalidate(() => true); scheduler.invalidate(() => true)
  first.resolve('old'); await settle()
  expect(load).toHaveBeenCalledTimes(2)
  expect(signal.aborted).toBe(false)
  trailing.resolve('current'); await settle()
  await vi.advanceTimersByTimeAsync(1000)
  expect(load).toHaveBeenCalledTimes(2)
  expect(observer.getCurrentResult().data).toBe('current')
  scheduler.cancel(); release(); owner.client.clear()
})

it('marks inactive and hidden cache entries stale without starting a GET, and fences queued work on cancellation', async () => {
  vi.useFakeTimers()
  const owner = session()
  const queryKey = scopedQueryKey(owner, ['api', 'tenant'], ['web-services', 'detail', 'service'])
  owner.client.setQueryData(queryKey, { value: 'saved', identity: JSON.stringify(queryKey) })
  const load = vi.fn(async () => 'fresh')
  const observer = new QueryObserver(owner.client, { queryKey, queryFn: load, staleTime: Infinity })
  const release = observer.subscribe(() => {})
  let visible = false
  const scheduler = new InvalidationScheduler(owner, () => visible)
  scheduler.invalidate(() => true)
  await vi.advanceTimersByTimeAsync(500)
  expect(load).not.toHaveBeenCalled()
  expect(owner.client.getQueryState(queryKey)?.isInvalidated).toBe(true)
  visible = true; scheduler.invalidate(() => true); scheduler.cancel()
  await vi.advanceTimersByTimeAsync(500)
  expect(load).not.toHaveBeenCalled()
  release(); scheduler.invalidate(() => true)
  await vi.advanceTimersByTimeAsync(500)
  expect(load).not.toHaveBeenCalled()
  owner.client.clear()
})
