// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createTestApp } from '../test/TestApp'
import * as api from '../api'
import { useGitIntegrationsManager } from '../modules/platform/useGitIntegrationsManager'
import type {
  GitProviderConnection,
  ProviderRepository,
  RepositoryAnalysis,
  SourceRepository,
} from '../types'
vi.mock('../api', () => ({
  fetchGitProviderConnections: vi.fn(),
  fetchSourceRepositories: vi.fn(),
  fetchProviderRepositories: vi.fn(),
  fetchProviderBranches: vi.fn(),
  analyzeProviderRepository: vi.fn(),
  beginGitHubInstallation: vi.fn(),
  linkProviderRepository: vi.fn(),
  disconnectGitProviderConnection: vi.fn(),
  reanalyzeSourceRepository: vi.fn(),
  unlinkSourceRepository: vi.fn(),
}))
let app: ReturnType<typeof createTestApp>
let state: ReturnType<typeof useGitIntegrationsManager>
let hidden = false
const connection = { id: 'connection', accountLogin: 'account' } as GitProviderConnection
const repository = (id: string): ProviderRepository => ({
  id,
  name: id,
  fullName: id,
  defaultBranch: 'main',
  htmlUrl: 'https://example.test',
  isPrivate: true,
})
const analysis = { branch: 'main', candidates: [{ candidateKey: 'plan' }] } as RepositoryAnalysis
const source = { id: 'source', connectionId: 'connection' } as SourceRepository
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((complete) => {
    resolve = complete
  })
  return { promise, resolve }
}
function Probe({
  token = 'token',
  tenant = 'alpha',
  canManage = true,
}: {
  token?: string
  tenant?: string
  canManage?: boolean
}) {
  const value = useGitIntegrationsManager({
    apiBaseUrl: '/api',
    token,
    tenantId: tenant,
    enabled: true,
    canManage,
  })
  useLayoutEffect(() => {
    state = value
  })
  return null
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.resetAllMocks()
  app = createTestApp()
  hidden = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  window.dispatchEvent(new Event('pageshow'))
  vi.mocked(api.fetchGitProviderConnections).mockResolvedValue([connection])
  vi.mocked(api.fetchSourceRepositories).mockResolvedValue([])
  vi.mocked(api.fetchProviderRepositories).mockResolvedValue({
    items: [repository('one'), repository('two')],
    page: 1,
    pageSize: 100,
    hasMore: true,
  })
  vi.mocked(api.fetchProviderBranches).mockResolvedValue({
    items: [{ name: 'main', commitSha: 'sha', isProtected: false }],
    page: 1,
    pageSize: 100,
    hasMore: false,
  })
  vi.mocked(api.analyzeProviderRepository).mockResolvedValue(analysis)
  vi.mocked(api.linkProviderRepository).mockResolvedValue(source)
})
afterEach(async () => {
  await act(async () => app.root.unmount())
  app.container.remove()
  vi.restoreAllMocks()
  vi.useRealTimers()
})
async function select() {
  await act(async () => app.root.render(<Probe />))
  await act(async () => state.selectRepository('one'))
  await act(async () => state.setSourceTechnicalName('application-source'))
}

it('preserves selection across token refresh and uses the latest credential for each mutation', async () => {
  await select()
  await act(async () => state.analyze())
  await act(async () => app.root.render(<Probe token="fresh" />))
  expect(state.selection.repositoryId).toBe('one')
  expect(state.selection.candidateKey).toBe('plan')
  await act(async () => state.link())
  expect(api.linkProviderRepository).toHaveBeenCalledWith(
    '/api',
    'connection',
    'one',
    'main',
    'application-source',
    'fresh',
    'alpha',
    expect.any(AbortSignal),
  )
  expect(api.fetchSourceRepositories).toHaveBeenLastCalledWith(
    '/api',
    'fresh',
    'alpha',
    expect.any(AbortSignal),
  )
})

it('aborts a selected repository analysis and rejects its result after the selection changes', async () => {
  const delayed = deferred<RepositoryAnalysis>()
  vi.mocked(api.analyzeProviderRepository).mockReturnValue(delayed.promise)
  await select()
  let action!: Promise<void>
  await act(async () => {
    action = state.analyze()
  })
  const signal = vi.mocked(api.analyzeProviderRepository).mock.calls[0][6]!
  await act(async () => state.selectRepository('two'))
  expect(signal.aborted).toBe(true)
  await act(async () => {
    delayed.resolve(analysis)
    await action
  })
  expect(state.selection.repositoryId).toBe('two')
  expect(state.selection.analysis).toBeNull()
})

it('does not refresh a different tenant after an accepted link finishes late', async () => {
  const delayed = deferred<SourceRepository>()
  vi.mocked(api.linkProviderRepository).mockReturnValue(delayed.promise)
  await select()
  await act(async () => state.analyze())
  let action!: Promise<void>
  await act(async () => {
    action = state.link()
  })
  const signal = vi.mocked(api.linkProviderRepository).mock.calls[0][7]!
  await act(async () => app.root.render(<Probe tenant="beta" />))
  const count = vi.mocked(api.fetchSourceRepositories).mock.calls.length
  expect(signal.aborted).toBe(true)
  await act(async () => {
    delayed.resolve(source)
    await action
  })
  expect(api.fetchSourceRepositories).toHaveBeenCalledTimes(count)
  expect(state.sourceRepositories).toEqual([])
  expect(state.selection.repositoryId).toBeNull()
})

it('keeps a durable write accepted when the following discovery read fails', async () => {
  await select()
  await act(async () => state.analyze())
  vi.mocked(api.fetchSourceRepositories).mockRejectedValue(
    new Error('Discovery temporarily unavailable'),
  )
  await act(async () => state.link())
  expect(state.actionStatus).toBe('success')
  expect(state.actionError).toBe('')
  expect(state.error).toContain('Discovery temporarily unavailable')
  expect(api.linkProviderRepository).toHaveBeenCalledTimes(1)
})

it('fences role loss and preserves explicit provider page numbers', async () => {
  await select()
  await act(async () => state.selectRepositoryPage(2))
  expect(api.fetchProviderRepositories).toHaveBeenLastCalledWith(
    '/api',
    'connection',
    'token',
    'alpha',
    expect.any(AbortSignal),
    2,
  )
  await act(async () => state.selectRepository('two'))
  await act(async () => state.analyze())
  await act(async () => app.root.render(<Probe canManage={false} />))
  expect(state.selection.analysis).toBeNull()
  await act(async () => state.link())
  expect(api.linkProviderRepository).not.toHaveBeenCalled()
})

it('refreshes external Git discovery at 60 seconds and pauses it while hidden', async () => {
  vi.useFakeTimers()
  await select()
  const repositories = vi.mocked(api.fetchProviderRepositories).mock.calls.length
  const branches = vi.mocked(api.fetchProviderBranches).mock.calls.length
  await act(async () => vi.advanceTimersByTimeAsync(59000))
  expect(api.fetchProviderRepositories).toHaveBeenCalledTimes(repositories)
  await act(async () => vi.advanceTimersByTimeAsync(1000))
  expect(api.fetchProviderRepositories).toHaveBeenCalledTimes(repositories + 1)
  expect(api.fetchProviderBranches).toHaveBeenCalledTimes(branches + 1)
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  vi.mocked(api.fetchProviderRepositories).mockClear()
  vi.mocked(api.fetchProviderBranches).mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(120000))
  await act(async () => state.refresh())
  expect(api.fetchProviderRepositories).not.toHaveBeenCalled()
  expect(api.fetchProviderBranches).not.toHaveBeenCalled()
})

it('preserves stale analysis for review without making it repository-link authority', async () => {
  vi.mocked(api.analyzeProviderRepository).mockResolvedValue({ ...analysis, commitSha: 'sha' })
  await select()
  await act(async () => state.analyze())
  expect(state.analysisStale).toBe(false)
  vi.mocked(api.fetchProviderBranches).mockResolvedValue({
    items: [{ name: 'main', commitSha: 'new-sha', isProtected: false }],
    page: 1,
    pageSize: 100,
    hasMore: false,
  })
  await act(async () => state.refresh())
  expect(state.selection.analysis?.commitSha).toBe('sha')
  expect(state.selection.candidateKey).toBe('plan')
  expect(state.analysisStale).toBe(true)
  await act(async () => state.link())
  expect(api.linkProviderRepository).toHaveBeenCalledTimes(1)
  vi.mocked(api.analyzeProviderRepository).mockResolvedValue({ ...analysis, commitSha: 'new-sha' })
  await act(async () => state.analyze())
  expect(state.analysisStale).toBe(false)
})

it('requires an explicit name and clears its draft when tenant changes', async () => {
  await act(async () => {
    app.root.render(<Probe />)
  })
  await act(async () => {
    await state.connectGitHub()
  })
  expect(api.beginGitHubInstallation).not.toHaveBeenCalled()
  await act(async () => {
    state.setTechnicalName('production-git')
  })
  expect(state.connectionNameValid).toBe(true)
  await act(async () => {
    app.root.render(<Probe tenant="beta" />)
  })
  expect(state.technicalName).toBe('')
  expect(state.connectionNameValid).toBe(false)
})

it('sends the explicit connection name with current tenant credentials', async () => {
  vi.mocked(api.beginGitHubInstallation).mockRejectedValue(new Error('stop before navigation'))
  await act(async () => {
    app.root.render(<Probe />)
  })
  await act(async () => {
    state.setTechnicalName('production-git')
  })
  await act(async () => {
    await state.connectGitHub()
  })
  expect(api.beginGitHubInstallation).toHaveBeenCalledWith(
    '/api',
    'production-git',
    'token',
    'alpha',
    expect.any(AbortSignal),
  )
})

it('links a named repository without build analysis and clears its name on selection change', async () => {
  await select()
  await act(async () => state.link())
  expect(api.analyzeProviderRepository).not.toHaveBeenCalled()
  expect(api.linkProviderRepository).toHaveBeenCalledWith(
    '/api',
    'connection',
    'one',
    'main',
    'application-source',
    'token',
    'alpha',
    expect.any(AbortSignal),
  )
  await act(async () => state.selectRepository('two'))
  expect(state.sourceTechnicalName).toBe('')
  expect(state.sourceNameValid).toBe(false)
  await act(async () => state.link())
  expect(api.linkProviderRepository).toHaveBeenCalledTimes(1)
})
