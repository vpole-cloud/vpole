// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { expect, it, vi } from 'vitest'
import { useResourceGroupChoices } from './useResourceGroupChoices'
import { createTestApp } from '../test/TestApp'
import { deferred, jsonResponse, strictFetch } from '../test/http'
import { resourceGroup } from '../test/fixtures'

it('uses the current credential on each page while preserving one authenticated scope', async () => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  const { root, container } = createTestApp()
  const first = deferred<Response>()
  const requests = strictFetch()
  requests.fetch.mockImplementation((url, init) => {
    const page = new URL(String(url)).searchParams.get('pageNumber')
    if (page === '1') return first.promise
    if (page === '2')
      return jsonResponse({
        items: [resourceGroup({ id: 'second' })],
        totalCount: 2,
        pageNumber: 2,
        pageSize: 200,
      })
    return requests.unhandled(url, init)
  })
  vi.stubGlobal('fetch', requests.fetch)
  let state!: ReturnType<typeof useResourceGroupChoices>
  function Probe({ token }: { token: string }) {
    const value = useResourceGroupChoices({
      apiBaseUrl: 'http://localhost',
      tenantId: 'tenant',
      token,
      enabled: true,
    })
    useLayoutEffect(() => {
      state = value
    })
    return null
  }
  try {
    await act(async () => root.render(<Probe token="old" />))
    await act(async () => root.render(<Probe token="new" />))
    await act(async () =>
      first.resolve(
        await jsonResponse({
          items: [resourceGroup()],
          totalCount: 2,
          pageNumber: 1,
          pageSize: 200,
        }),
      ),
    )
    expect(state.items.map((item) => item.id)).toEqual(['group', 'second'])
    expect(requests.fetch.mock.calls).toHaveLength(2)
    expect(requests.fetch.mock.calls[1][1]?.headers).toMatchObject({ Authorization: 'Bearer new' })
    requests.verify()
  } finally {
    await act(async () => root.unmount())
    container.remove()
    vi.unstubAllGlobals()
  }
})
