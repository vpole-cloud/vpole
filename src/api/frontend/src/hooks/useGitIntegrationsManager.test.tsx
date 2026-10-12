// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createTestApp } from '../test/TestApp'
import * as api from '../api'
import { useGitIntegrationsManager } from '../modules/platform/useGitIntegrationsManager'
import type { GitProviderConnection, ProviderRepository, RepositoryAnalysis, SourceRepository } from '../types'
vi.mock('../api', () => ({ fetchGitProviderConnections: vi.fn(), fetchSourceRepositories: vi.fn(), fetchProviderRepositories: vi.fn(), fetchProviderBranches: vi.fn(), analyzeProviderRepository: vi.fn(), beginGitHubInstallation: vi.fn(), linkProviderRepository: vi.fn(), disconnectGitProviderConnection: vi.fn(), reanalyzeSourceRepository: vi.fn(), unlinkSourceRepository: vi.fn() }))
let app: ReturnType<typeof createTestApp>
let state: ReturnType<typeof useGitIntegrationsManager>
const connection = { id: 'connection', accountLogin: 'account' } as GitProviderConnection
const repository = (id: string): ProviderRepository => ({ id, name: id, fullName: id, defaultBranch: 'main', htmlUrl: 'https://example.test', isPrivate: true })
const analysis = { branch: 'main', candidates: [{ candidateKey: 'plan' }] } as RepositoryAnalysis
const source = { id: 'source', connectionId: 'connection' } as SourceRepository
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((complete) => { resolve = complete }); return { promise, resolve } }
function Probe({ token = 'token', tenant = 'alpha', canManage = true }: { token?: string; tenant?: string; canManage?: boolean }) {
  const value = useGitIntegrationsManager({ apiBaseUrl: '/api', token, tenantId: tenant, enabled: true, canManage })
  useLayoutEffect(() => { state = value })
  return null
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true }); vi.resetAllMocks(); app = createTestApp()
  vi.mocked(api.fetchGitProviderConnections).mockResolvedValue([connection])
  vi.mocked(api.fetchSourceRepositories).mockResolvedValue([])
  vi.mocked(api.fetchProviderRepositories).mockResolvedValue({ items: [repository('one'), repository('two')], page: 1, pageSize: 100, hasMore: true })
  vi.mocked(api.fetchProviderBranches).mockResolvedValue({ items: [{ name: 'main', commitSha: 'sha', isProtected: false }], page: 1, pageSize: 100, hasMore: false })
  vi.mocked(api.analyzeProviderRepository).mockResolvedValue(analysis)
  vi.mocked(api.linkProviderRepository).mockResolvedValue(source)
})
afterEach(async () => { await act(async () => app.root.unmount()); app.container.remove() })
async function select() { await act(async () => app.root.render(<Probe />)); await act(async () => state.selectRepository('one')) }

it('preserves selection across token refresh and uses the latest credential for each mutation', async () => {
  await select(); await act(async () => state.analyze())
  await act(async () => app.root.render(<Probe token="fresh" />))
  expect(state.selection.repositoryId).toBe('one'); expect(state.selection.candidateKey).toBe('plan')
  await act(async () => state.link())
  expect(api.linkProviderRepository).toHaveBeenCalledWith('/api', 'connection', 'one', 'main', 'plan', 'fresh', 'alpha', expect.any(AbortSignal))
  expect(api.fetchSourceRepositories).toHaveBeenLastCalledWith('/api', 'fresh', 'alpha', expect.any(AbortSignal))
})

it('aborts a selected repository analysis and rejects its result after the selection changes', async () => {
  const delayed = deferred<RepositoryAnalysis>(); vi.mocked(api.analyzeProviderRepository).mockReturnValue(delayed.promise)
  await select(); let action!: Promise<void>; await act(async () => { action = state.analyze() })
  const signal = vi.mocked(api.analyzeProviderRepository).mock.calls[0][6]!
  await act(async () => state.selectRepository('two')); expect(signal.aborted).toBe(true)
  await act(async () => { delayed.resolve(analysis); await action })
  expect(state.selection.repositoryId).toBe('two'); expect(state.selection.analysis).toBeNull()
})

it('does not refresh a different tenant after an accepted link finishes late', async () => {
  const delayed = deferred<SourceRepository>(); vi.mocked(api.linkProviderRepository).mockReturnValue(delayed.promise)
  await select(); await act(async () => state.analyze()); let action!: Promise<void>
  await act(async () => { action = state.link() }); const signal = vi.mocked(api.linkProviderRepository).mock.calls[0][7]!
  await act(async () => app.root.render(<Probe tenant="beta" />)); const count = vi.mocked(api.fetchSourceRepositories).mock.calls.length
  expect(signal.aborted).toBe(true)
  await act(async () => { delayed.resolve(source); await action })
  expect(api.fetchSourceRepositories).toHaveBeenCalledTimes(count)
  expect(state.sourceRepositories).toEqual([]); expect(state.selection.repositoryId).toBeNull()
})

it('keeps a durable write accepted when the following discovery read fails', async () => {
  await select(); await act(async () => state.analyze())
  vi.mocked(api.fetchSourceRepositories).mockRejectedValue(new Error('Discovery temporarily unavailable'))
  await act(async () => state.link())
  expect(state.actionStatus).toBe('success'); expect(state.actionError).toBe('')
  expect(state.error).toContain('Discovery temporarily unavailable')
  expect(api.linkProviderRepository).toHaveBeenCalledTimes(1)
})

it('fences role loss and preserves explicit provider page numbers', async () => {
  await select(); await act(async () => state.selectRepositoryPage(2))
  expect(api.fetchProviderRepositories).toHaveBeenLastCalledWith('/api', 'connection', 'token', 'alpha', expect.any(AbortSignal), 2)
  await act(async () => state.selectRepository('two')); await act(async () => state.analyze())
  await act(async () => app.root.render(<Probe canManage={false} />))
  expect(state.selection.analysis).toBeNull()
  await act(async () => state.link()); expect(api.linkProviderRepository).not.toHaveBeenCalled()
})
