import { vi } from 'vitest'

export function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((accept, fail) => {
    resolve = accept
    reject = fail
  })
  return { promise, resolve, reject }
}

export const jsonResponse = (body: unknown, status = 200) =>
  Promise.resolve(
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }),
  )

// Also assert after rendering: application error boundaries can catch a failed fetch.
export function strictFetch() {
  const unexpected: string[] = []
  const unhandled = (url: RequestInfo | URL, init?: RequestInit): never => {
    const request = `${init?.method ?? 'GET'} ${String(url)}`
    unexpected.push(request)
    throw new Error(`Unexpected request: ${request}`)
  }
  const fetch = vi.fn<(url: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(unhandled)
  return {
    fetch,
    unhandled,
    reset: () => {
      unexpected.length = 0
      fetch.mockReset().mockImplementation(unhandled)
    },
    verify: () => {
      if (unexpected.length) throw new Error(`Unexpected requests:\n${unexpected.join('\n')}`)
    },
  }
}
