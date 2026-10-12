// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { SessionQueries } from '../../data/SessionQueries'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import * as api from '../../api'
import { useGitSourceSelection } from './useGitSourceSelection'
import type { RepositoryAnalysis } from '../../types'
vi.mock('../../api', () => ({ analyzeProviderRepository: vi.fn(), fetchGitProviderConnections: vi.fn(), fetchProviderBranches: vi.fn(),
  fetchProviderRepositories: vi.fn(), fetchSourceRepositories: vi.fn(), linkProviderRepository: vi.fn(), reanalyzeSourceRepository: vi.fn() }))
let root: Root
let state!: ReturnType<typeof useGitSourceSelection>
function ProbeContent({ tenant = 'alpha', token = 'token' }: { tenant?: string; token?: string }) { const value = useGitSourceSelection({ baseUrl: '/api', token, tenantId: tenant }); useLayoutEffect(() => { state = value }); return null }
function Probe({ tenant = 'alpha', token = 'token' }: { tenant?: string; token?: string }) { return <SessionQueries identity={`test:${tenant}`}><ProbeContent tenant={tenant} token={token} /></SessionQueries> }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done }); return { promise, resolve } }
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true }); vi.resetAllMocks(); root = createRoot(document.createElement('div'))
  vi.mocked(api.fetchGitProviderConnections).mockResolvedValue([{ id: 'account', accountLogin: 'team', provider: 'GitHub', accountType: 'Organization', repositorySelection: 'All', createdAtUtc: '', updatedAtUtc: '' }])
  vi.mocked(api.fetchSourceRepositories).mockResolvedValue([])
  vi.mocked(api.fetchProviderRepositories).mockResolvedValue({ items: [{ id: 'repo', name: 'app', fullName: 'team/app', htmlUrl: '', isPrivate: true, defaultBranch: 'main' }], page: 1, pageSize: 100, hasMore: false })
  vi.mocked(api.fetchProviderBranches).mockResolvedValue({ items: [{ name: 'main', commitSha: 'abc', isProtected: false }], page: 1, pageSize: 100, hasMore: false })
})
afterEach(async () => { await act(async () => root.unmount()); vi.restoreAllMocks() })
it('ignores analysis if selection changed away and back while the old request was running', async () => {
  const old = deferred<RepositoryAnalysis>(); vi.mocked(api.analyzeProviderRepository).mockReturnValue(old.promise)
  await act(async () => root.render(<Probe />)); await act(async () => state.selectRepository('repo'))
  let pending!: Promise<void>; await act(async () => { pending = state.analyze() })
  await act(async () => state.selectBranch('other')); await act(async () => state.selectBranch('main'))
  await act(async () => { old.resolve({ repositoryId: 'repo', repositoryFullName: 'team/app', branch: 'main', commitSha: 'old', analyzedAtUtc: '', detectedFiles: [], candidates: [] }); await pending })
  expect(state.selection.analysis).toBeNull(); expect(state.busy).toBe(false)
})
it('loads further Git repository pages and cancels stale branch reads', async () => {
  const pending = deferred<Awaited<ReturnType<typeof api.fetchProviderBranches>>>()
  vi.mocked(api.fetchProviderRepositories).mockResolvedValueOnce({ items: [{ id: 'repo', name: 'app', fullName: 'team/app', htmlUrl: '', isPrivate: true, defaultBranch: 'main' }], page: 1, pageSize: 100, hasMore: true })
    .mockResolvedValueOnce({ items: [{ id: 'second', name: 'second', fullName: 'team/second', htmlUrl: '', isPrivate: false, defaultBranch: 'main' }], page: 2, pageSize: 100, hasMore: false })
  vi.mocked(api.fetchProviderBranches).mockReturnValueOnce(pending.promise)
  await act(async () => root.render(<Probe />)); expect(state.moreRepositories).toBe(true)
  await act(async () => state.loadMoreRepositories()); expect(state.repositories.map((item) => item.id)).toEqual(['repo', 'second'])
  expect(vi.mocked(api.fetchProviderRepositories).mock.calls[1][5]).toBe(2)
  await act(async () => state.selectRepository('repo')); const signal = vi.mocked(api.fetchProviderBranches).mock.calls[0][5]!
  await act(async () => state.selectRepository('second')); expect(signal.aborted).toBe(true)
  await act(async () => pending.resolve({ items: [{ name: 'stale-branch', commitSha: 'old', isProtected: false }], page: 1, pageSize: 100, hasMore: false }))
  expect(state.branches.map((item) => item.name)).toEqual(['main'])
})
it('aborts tenant source discovery and ignores late old-tenant data', async () => {
  const old = deferred<Awaited<ReturnType<typeof api.fetchSourceRepositories>>>()
  vi.mocked(api.fetchSourceRepositories).mockReturnValueOnce(old.promise).mockResolvedValueOnce([])
  await act(async () => root.render(<Probe />)); const signal = vi.mocked(api.fetchSourceRepositories).mock.calls[0][3]!
  await act(async () => root.render(<Probe tenant="beta" />)); expect(signal.aborted).toBe(true)
  await act(async () => old.resolve([])); expect(state.status).toBe('success'); expect(state.sources).toEqual([])
})

it('refreshes an existing linked source when the user selects a newly analyzed commit', async () => {
  const plan = { candidateKey: 'docker', kind: 'Dockerfile' as const, displayName: 'Dockerfile', sourceRoot: '.', manifestPath: 'Dockerfile', confidence: 'High' as const,
    signals: [], warnings: [], commands: [], dockerfilePreview: 'FROM scratch' }
  const source = { id: 'source', connectionId: 'account', externalRepositoryId: 'repo', repositoryFullName: 'team/app', repositoryUrl: '', isPrivate: true,
    branch: 'main', commitSha: 'old', createdAtUtc: '', updatedAtUtc: '', analyzedAtUtc: '', selectedPlan: plan }
  vi.mocked(api.fetchSourceRepositories).mockResolvedValue([source])
  vi.mocked(api.analyzeProviderRepository).mockResolvedValue({ repositoryId: 'repo', repositoryFullName: 'team/app', branch: 'main', commitSha: 'new', analyzedAtUtc: '', detectedFiles: [], candidates: [plan] })
  vi.mocked(api.reanalyzeSourceRepository).mockImplementation(async () => { const updated = { ...source, commitSha: 'new' }; vi.mocked(api.fetchSourceRepositories).mockResolvedValue([updated]); return updated })
  await act(async () => root.render(<Probe />)); await act(async () => state.selectRepository('repo')); await act(async () => { await state.analyze() })
  let selected!: Awaited<ReturnType<typeof state.link>>; await act(async () => { selected = await state.link() })
  expect(selected?.commitSha).toBe('new'); expect(state.sources[0].commitSha).toBe('new')
  expect(api.reanalyzeSourceRepository).toHaveBeenCalledWith('/api', 'source', 'token', 'alpha'); expect(api.linkProviderRepository).not.toHaveBeenCalled()
})


it('keeps branch and build-plan selection across credential refresh and links with the current token', async () => {
  const plan = { candidateKey: 'docker', kind: 'Dockerfile' as const, displayName: 'Dockerfile', sourceRoot: '.', manifestPath: 'Dockerfile', confidence: 'High' as const,
    signals: [], warnings: [], commands: [], dockerfilePreview: 'FROM scratch' }
  vi.mocked(api.analyzeProviderRepository).mockResolvedValue({ repositoryId: 'repo', repositoryFullName: 'team/app', branch: 'main', commitSha: 'commit', analyzedAtUtc: '', detectedFiles: [], candidates: [plan] })
  const source = { id: 'source', connectionId: 'account', externalRepositoryId: 'repo', repositoryFullName: 'team/app', repositoryUrl: '', isPrivate: true, branch: 'main', commitSha: 'commit', analyzedAtUtc: '', createdAtUtc: '', updatedAtUtc: '', selectedPlan: plan }
  vi.mocked(api.linkProviderRepository).mockImplementation(async () => { vi.mocked(api.fetchSourceRepositories).mockResolvedValue([source]); return source })
  await act(async () => root.render(<Probe />)); await act(async () => state.selectRepository('repo')); await act(async () => state.analyze())
  await act(async () => root.render(<Probe token="renewed-token" />))
  expect(state.selection.repositoryId).toBe('repo'); expect(state.selection.branch).toBe('main'); expect(state.selection.candidateKey).toBe('docker')
  await act(async () => state.link())
  expect(api.linkProviderRepository).toHaveBeenCalledWith('/api', 'account', 'repo', 'main', 'docker', 'renewed-token', 'alpha')
})
