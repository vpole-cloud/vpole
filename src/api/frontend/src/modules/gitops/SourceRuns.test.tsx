// @vitest-environment jsdom
import { act } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import { createTestApp } from '../../test/TestApp'
import { deferred, jsonResponse, strictFetch } from '../../test/http'
import { gitOpsTestServices } from './testServices'
import { gitOpsRunFixture, gitOpsSourceFixture } from './testFixtures'
import { SourceRuns } from './SourceRuns'
let app: ReturnType<typeof createTestApp>
const requests = strictFetch()
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperty(document, 'hidden', { configurable: true, value: false })
  vi.useFakeTimers()
  requests.reset()
  vi.stubGlobal('fetch', requests.fetch)
  app = createTestApp()
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
  requests.verify()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})
it('reads final receipts when a run completes after its last operation poll', async () => {
  const completion = deferred<Response>()
  let complete = false
  let runReads = 0
  const run = { ...gitOpsRunFixture, status: 'Applying' }
  requests.fetch.mockImplementation((input, init) => {
    const path = new URL(String(input)).pathname
    if (path.endsWith('/runs'))
      return jsonResponse({ items: [run], page: 1, pageSize: 50, hasMore: false })
    if (path.endsWith('/runs/run')) return ++runReads === 1 ? jsonResponse(run) : completion.promise
    if (path.endsWith('/operations'))
      return jsonResponse({
        items: [
          {
            kind: 'ResourceGroup',
            name: 'group',
            resourceId: 'group',
            phase: 'Prepare',
            status: complete ? 'Succeeded' : 'Pending',
            blocker: null,
            changed: complete,
            plan: null,
            lifecycle: null,
          },
        ],
        page: 1,
        pageSize: 50,
        hasMore: false,
      })
    return requests.unhandled(input, init)
  })
  const services = gitOpsTestServices()
  services.navigation.search = '?resource=source&section=runs&selectionSource=source&run=run'
  await act(async () =>
    app.root.render(
      <AppContext.Provider value={services}>
        <SourceRuns
          source={gitOpsSourceFixture}
          active
          healthy
          onRun={vi.fn()}
          onDeleted={vi.fn()}
          refreshSource={vi.fn()}
        />
      </AppContext.Provider>,
    ),
  )
  await act(async () => {
    await vi.advanceTimersByTimeAsync(5001)
  })
  const operation = () =>
    app.container.querySelector('[data-testid="gitops-operation-group-Prepare"]')!
  expect(operation().textContent).toContain('Pending')
  complete = true
  await act(async () =>
    completion.resolve(await jsonResponse({ ...run, status: 'ConfigurationApplied' })),
  )
  expect(operation().textContent).toContain('Succeeded')
})
