// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import * as api from '../api'
import { useSourceBuildsManager } from './useSourceBuildsManager'
import type { SourceBuildJob, SourceRepository } from '../types'
vi.mock('../api', () => ({ createSourceBuildConfiguration: vi.fn(), deleteSourceBuildConfiguration: vi.fn(), fetchSourceBuildConfigurations: vi.fn(),
  fetchSourceBuildJobs: vi.fn(), fetchSourceRepositories: vi.fn(), startSourceBuild: vi.fn(), updateSourceBuildRecipe: vi.fn(), reanalyzeSourceRepository: vi.fn() }))
let root: Root
let state!: ReturnType<typeof useSourceBuildsManager>
const job: SourceBuildJob = { id: 'job', configurationId: 'configuration', sourceRepositoryId: 'source', webServiceId: 'service', branch: 'main', commitSha: 'abc',
  createdAtUtc: '', updatedAtUtc: '', recipe: { kind: 0, candidateKey: 'docker', sourceRoot: '.', manifestPath: 'Dockerfile', commands: [], dockerfile: 'FROM scratch', isCustomized: false },
  state: { status: 'queued', startedAtUtc: '' } }
function Probe({ tenant = 'alpha' }: { tenant?: string }) { const value = useSourceBuildsManager({ apiBaseUrl: '/api', token: 'token', tenantId: tenant, webServiceId: 'service', enabled: true }); useLayoutEffect(() => { state = value }); return null }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done }); return { promise, resolve } }
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true }); vi.resetAllMocks(); root = createRoot(document.createElement('div'))
  vi.mocked(api.fetchSourceBuildConfigurations).mockResolvedValue([]); vi.mocked(api.fetchSourceRepositories).mockResolvedValue([])
  vi.mocked(api.fetchSourceBuildJobs).mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 20 })
  vi.mocked(api.startSourceBuild).mockResolvedValue(job)
})
afterEach(async () => { await act(async () => root.unmount()); vi.restoreAllMocks() })
it('does not replace an accepted queued job with an earlier history response', async () => {
  const old = deferred<Awaited<ReturnType<typeof api.fetchSourceBuildJobs>>>()
  vi.mocked(api.fetchSourceBuildJobs).mockReturnValueOnce(old.promise)
  await act(async () => root.render(<Probe />)); const signal = vi.mocked(api.fetchSourceBuildJobs).mock.calls[0][4]!
  await act(async () => { await state.start('configuration') }); expect(signal.aborted).toBe(true)
  await act(async () => old.resolve({ items: [], totalCount: 0, pageNumber: 1, pageSize: 20 }))
  expect(state.jobs.map((item) => item.id)).toEqual(['job']); expect(state.hasActiveBuild).toBe(true); expect(state.jobsStatus).toBe('success')
})
it('ignores an accepted old mutation after tenant changes away and back', async () => {
  const old = deferred<SourceBuildJob>(); vi.mocked(api.startSourceBuild).mockReturnValueOnce(old.promise)
  await act(async () => root.render(<Probe />)); let pending!: Promise<SourceBuildJob>
  await act(async () => { pending = state.start('configuration') })
  await act(async () => root.render(<Probe tenant="beta" />)); await act(async () => root.render(<Probe tenant="alpha" />))
  await act(async () => { old.resolve(job); await pending })
  expect(state.jobs).toEqual([]); expect(state.actionStatus).toBe('idle')
})
it('rejects failed configuration writes so callers retain their source selection', async () => {
  vi.mocked(api.createSourceBuildConfiguration).mockRejectedValueOnce(new Error('Source is unavailable'))
  await act(async () => root.render(<Probe />))
  await act(async () => { await expect(state.create('source', 'service')).rejects.toThrow('Source is unavailable') })
  expect(state.actionStatus).toBe('error'); expect(state.actionError).toBe('Source is unavailable'); expect(state.configurations).toEqual([])
})

it('retains a newly analyzed commit when an earlier configuration refresh completes', async () => {
  const source: SourceRepository = { id: 'source', connectionId: 'account', externalRepositoryId: 'repo', repositoryFullName: 'team/app', repositoryUrl: '', isPrivate: true,
    branch: 'main', commitSha: 'old', createdAtUtc: '', updatedAtUtc: '', analyzedAtUtc: '',
    selectedPlan: { candidateKey: 'docker', kind: 'Dockerfile', displayName: 'Dockerfile', sourceRoot: '.', manifestPath: 'Dockerfile', confidence: 'High', signals: [], warnings: [], commands: [], dockerfilePreview: 'FROM scratch' } }
  const old = deferred<SourceRepository[]>()
  vi.mocked(api.fetchSourceRepositories).mockResolvedValueOnce([source]).mockReturnValueOnce(old.promise)
  vi.mocked(api.reanalyzeSourceRepository).mockResolvedValue({ ...source, commitSha: 'new' })
  await act(async () => root.render(<Probe />))
  let pending!: ReturnType<typeof state.refresh>
  await act(async () => { pending = state.refresh() })
  const signal = vi.mocked(api.fetchSourceRepositories).mock.calls[1][3]!
  await act(async () => { await state.reanalyze('source') }); expect(signal.aborted).toBe(true)
  await act(async () => { old.resolve([source]); await pending })
  expect(state.sourceRepositories[0].commitSha).toBe('new'); expect(state.status).toBe('success')
})
