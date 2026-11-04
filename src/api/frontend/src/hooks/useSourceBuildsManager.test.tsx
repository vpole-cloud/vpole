// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { SessionQueries } from '../data/SessionQueries'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import * as api from '../api'
import * as buildApi from '../modules/web-services/buildApi'
import { useSourceBuildsManager } from '../modules/web-services/useSourceBuilds'
import type { SourceBuildJob, SourceRepository } from '../types'
vi.mock('../api', () => ({ fetchSourceRepositories: vi.fn(), reanalyzeSourceRepository: vi.fn() }))
vi.mock('../modules/web-services/buildApi', () => ({
  createSourceBuildConfiguration: vi.fn(),
  deleteSourceBuildConfiguration: vi.fn(),
  fetchSourceBuildConfigurations: vi.fn(),
  fetchSourceBuildJobs: vi.fn(),
  startSourceBuild: vi.fn(),
  updateSourceBuildRecipe: vi.fn(),
}))
let root: Root
let state!: ReturnType<typeof useSourceBuildsManager>
const job: SourceBuildJob = {
  id: 'job',
  configurationId: 'configuration',
  sourceRepositoryId: 'source',
  webServiceId: 'service',
  branch: 'main',
  commitSha: 'abc',
  createdAtUtc: '',
  updatedAtUtc: '',
  recipe: {
    dockerfile: 'FROM scratch',
  },
  state: { status: 'queued', startedAtUtc: '' },
}
function ProbeContent({
  tenant = 'alpha',
  enabled = true,
}: {
  tenant?: string
  enabled?: boolean
}) {
  const value = useSourceBuildsManager({
    apiBaseUrl: '/api',
    token: 'token',
    tenantId: tenant,
    webServiceId: 'service',
    enabled,
  })
  useLayoutEffect(() => {
    state = value
  })
  return null
}
function Probe({ tenant = 'alpha', enabled = true }: { tenant?: string; enabled?: boolean }) {
  return (
    <SessionQueries identity={`test:${tenant}`}>
      <ProbeContent tenant={tenant} enabled={enabled} />
    </SessionQueries>
  )
}
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => {
    resolve = done
  })
  return { promise, resolve }
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.resetAllMocks()
  root = createRoot(document.createElement('div'))
  vi.mocked(buildApi.fetchSourceBuildConfigurations).mockResolvedValue([])
  vi.mocked(api.fetchSourceRepositories).mockResolvedValue([])
  vi.mocked(buildApi.fetchSourceBuildJobs).mockResolvedValue({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 20,
  })
  vi.mocked(buildApi.startSourceBuild).mockImplementation(async () => {
    vi.mocked(buildApi.fetchSourceBuildJobs).mockResolvedValue({
      items: [job],
      totalCount: 1,
      pageNumber: 1,
      pageSize: 20,
    })
    return job
  })
})
afterEach(async () => {
  await act(async () => root.unmount())
  vi.restoreAllMocks()
})
it('does not replace an accepted queued job with an earlier history response', async () => {
  const old = deferred<Awaited<ReturnType<typeof buildApi.fetchSourceBuildJobs>>>()
  vi.mocked(buildApi.fetchSourceBuildJobs).mockReturnValueOnce(old.promise)
  await act(async () => root.render(<Probe />))
  const signal = vi.mocked(buildApi.fetchSourceBuildJobs).mock.calls[0][4]!
  await act(async () => {
    await state.start('configuration')
  })
  expect(signal.aborted).toBe(true)
  await act(async () => old.resolve({ items: [], totalCount: 0, pageNumber: 1, pageSize: 20 }))
  expect(state.jobs.map((item) => item.id)).toEqual(['job'])
  expect(state.hasActiveBuild).toBe(true)
  expect(state.jobsStatus).toBe('success')
})
it('ignores an accepted old mutation after tenant changes away and back', async () => {
  const old = deferred<SourceBuildJob>()
  vi.mocked(buildApi.startSourceBuild).mockReturnValueOnce(old.promise)
  await act(async () => root.render(<Probe />))
  let pending!: Promise<SourceBuildJob>
  await act(async () => {
    pending = state.start('configuration')
  })
  await act(async () => root.render(<Probe tenant="beta" />))
  await act(async () => root.render(<Probe tenant="alpha" />))
  await act(async () => {
    old.resolve(job)
    await expect(pending).rejects.toThrow('Aborted')
  })
  expect(state.jobs).toEqual([])
  expect(state.actionStatus).toBe('idle')
})
it('rejects failed configuration writes so callers retain their source selection', async () => {
  vi.mocked(buildApi.createSourceBuildConfiguration).mockRejectedValueOnce(
    new Error('Source is unavailable'),
  )
  await act(async () => root.render(<Probe />))
  await act(async () => {
    await expect(
      state.create('source', 'service', 'api-build', { dockerfile: 'FROM scratch' }),
    ).rejects.toThrow('Source is unavailable')
  })
  expect(state.actionStatus).toBe('error')
  expect(state.actionError).toBe('Source is unavailable')
  expect(state.configurations).toEqual([])
})

it('analysis remains authoring output and does not replace the repository identity cache', async () => {
  const source: SourceRepository = {
    id: 'source',
    technicalName: 'application',
    revision: 1,
    connectionId: 'account',
    externalRepositoryId: 'repo',
    repositoryFullName: 'team/app',
    repositoryUrl: '',
    isPrivate: true,
    branch: 'main',
    createdAtUtc: '',
    updatedAtUtc: '',
  }
  vi.mocked(api.fetchSourceRepositories).mockResolvedValue([source])
  vi.mocked(api.reanalyzeSourceRepository).mockResolvedValue({
    repositoryId: 'repo',
    repositoryFullName: 'team/app',
    branch: 'main',
    commitSha: 'new',
    analyzedAtUtc: '',
    detectedFiles: [],
    candidates: [],
  })
  await act(async () => root.render(<Probe />))
  await act(async () => {
    await state.reanalyze('source')
  })
  expect(state.sourceRepositories).toEqual([source])
})

it('retains loaded configuration and history while a visited Builds section becomes inactive', async () => {
  const configuration = {
    id: 'configuration',
    technicalName: 'api-build',
    revision: 1,
    webServiceId: 'service',
    sourceRepositoryId: 'source',
    recipe: job.recipe,
    createdByUserId: 'user',
    createdAtUtc: '',
    updatedAtUtc: '',
  }
  vi.mocked(buildApi.fetchSourceBuildConfigurations).mockResolvedValue([configuration])
  vi.mocked(buildApi.fetchSourceBuildJobs).mockResolvedValue({
    items: [job],
    totalCount: 1,
    pageNumber: 1,
    pageSize: 20,
  })
  await act(async () => root.render(<Probe />))
  const reads = vi.mocked(buildApi.fetchSourceBuildJobs).mock.calls.length
  await act(async () => root.render(<Probe enabled={false} />))
  expect(state.configurations[0].id).toBe('configuration')
  expect(state.jobs[0].id).toBe('job')
  expect(vi.mocked(buildApi.fetchSourceBuildJobs).mock.calls).toHaveLength(reads)
})
