import { useState } from 'react'
import { fetchSourceRepositories, reanalyzeSourceRepository } from '../../api'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useVisibleRefresh } from '../../hooks/useVisibleRefresh'
import type { LoadStatus, SourceRepository } from '../../types'
import {
  createSourceBuildConfiguration,
  deleteSourceBuildConfiguration,
  fetchSourceBuildConfigurations,
  fetchSourceBuildJobs,
  startSourceBuild,
  updateSourceBuildRecipe,
} from './buildApi'
import type { SourceBuildConfiguration, SourceBuildJob, SourceBuildRecipe } from './buildTypes'
import { invalidateWebServiceState } from './invalidate'
import { gitInterests } from '../platform/gitRealtime'

interface Options {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  webServiceId?: string | null
  enabled: boolean
  canManage?: boolean
}
const terminal = new Set(['succeeded', 'failed'])

export function useSourceBuildsManager({
  apiBaseUrl,
  token,
  tenantId,
  webServiceId,
  enabled,
  canManage = true,
}: Options) {
  const [visited, setVisited] = useState(enabled)
  if (enabled && !visited) setVisited(true)
  const scope = useApiScope({ baseUrl: apiBaseUrl, token, tenantId })
  const queryScope = [apiBaseUrl, tenantId]
  const configurations = useScopedQuery({
    scope: queryScope,
    key: ['source-builds', 'configurations'],
    enabled: visited,
    active: enabled,
    realtime: [{ topic: 'source-builds', tenantId }],
    load: (signal) =>
      fetchSourceBuildConfigurations(apiBaseUrl, scope.credential(), tenantId, signal),
  })
  const sources = useScopedQuery({
    scope: queryScope,
    key: ['git', 'sources'],
    enabled: visited,
    active: enabled,
    realtime: gitInterests(tenantId, ['git', 'sources']),
    load: (signal) => fetchSourceRepositories(apiBaseUrl, scope.credential(), tenantId, signal),
  })
  const jobs = useScopedQuery({
    scope: queryScope,
    key: ['source-builds', 'jobs', webServiceId],
    enabled: visited && Boolean(webServiceId),
    active: enabled,
    realtime: [{ topic: 'source-builds', tenantId, resourceId: webServiceId ?? undefined }],
    load: (signal) =>
      fetchSourceBuildJobs(apiBaseUrl, webServiceId!, scope.credential(), tenantId, signal),
  })
  const mutation = useScopedMutation(`${scope.identity}:${webServiceId}:source-builds:${canManage}`)
  const [actionStatus, setActionStatus] = useState<LoadStatus>('idle')
  const visibleJobs = jobs.resource?.items ?? []
  const hasActiveBuild = visibleJobs.some((job) => !terminal.has(job.state.status))
  useVisibleRefresh(enabled && hasActiveBuild, jobs.refresh, 2500, [
    { topic: 'source-builds', tenantId, resourceId: webServiceId ?? undefined },
  ])

  const setRead = async <T>(queryKey: readonly unknown[], value: T, guard: () => void) => {
    await scope.session.client.cancelQueries({ queryKey, exact: true })
    guard()
    scope.session.client.setQueryData(queryKey, { value, identity: JSON.stringify(queryKey) })
  }
  const action = async <T>(
    run: () => Promise<T>,
    accepted: (value: T, guard: () => void) => Promise<void>,
  ) => {
    if (!canManage) throw new Error('Tenant viewer access is read-only.')
    let guard = () => {}
    const value = await mutation.run(
      async (signal) => {
        guard = () => {
          if (signal.aborted || !scope.isCurrent()) throw new DOMException('Aborted', 'AbortError')
        }
        guard()
        setActionStatus('loading')
        const value = await run()
        guard()
        return value
      },
      async (value) => {
        guard()
        await accepted(value, guard)
        guard()
        if (!scope.isCurrent()) return
        setActionStatus('success')
        await invalidateWebServiceState(scope, [['git', 'sources']])
      },
    )
    if (!scope.isCurrent() || value === undefined) throw new DOMException('Aborted', 'AbortError')
    return value
  }
  const configs = configurations.resource ?? []
  const repositories = sources.resource ?? []
  return {
    configurations: configs,
    sourceRepositories: repositories,
    jobs: visibleJobs,
    status:
      configurations.status === 'error' || sources.status === 'error'
        ? ('error' as const)
        : configurations.isInitialLoading || sources.isInitialLoading
          ? ('loading' as const)
          : ('success' as const),
    jobsStatus: jobs.status,
    actionStatus: mutation.busy
      ? ('loading' as const)
      : mutation.error
        ? ('error' as const)
        : actionStatus === 'loading'
          ? ('idle' as const)
          : actionStatus,
    error: configurations.error || sources.error,
    jobsError: jobs.error,
    actionError: mutation.error instanceof Error ? mutation.error.message : '',
    hasActiveBuild,
    refresh: () => Promise.all([configurations.refresh(), sources.refresh(), jobs.refresh()]),
    create: (sourceId: string, serviceId: string) =>
      action(
        () =>
          createSourceBuildConfiguration(
            apiBaseUrl,
            sourceId,
            serviceId,
            scope.credential(),
            tenantId,
          ),
        (created: SourceBuildConfiguration, guard) =>
          setRead(
            configurations.queryKey,
            [created, ...configs.filter((item) => item.id !== created.id)],
            guard,
          ),
      ),
    remove: (id: string) =>
      action(
        () =>
          deleteSourceBuildConfiguration(apiBaseUrl, id, scope.credential(), tenantId).then(
            () => id,
          ),
        (removed, guard) =>
          setRead(
            configurations.queryKey,
            configs.filter((item) => item.id !== removed),
            guard,
          ),
      ),
    start: (id: string) =>
      action(
        () => startSourceBuild(apiBaseUrl, id, scope.credential(), tenantId),
        (created: SourceBuildJob, guard) =>
          setRead(
            jobs.queryKey,
            {
              ...(jobs.resource ?? { pageNumber: 1, pageSize: 20, totalCount: 0 }),
              totalCount: (jobs.resource?.totalCount ?? 0) + 1,
              items: [created, ...visibleJobs.filter((item) => item.id !== created.id)],
            },
            guard,
          ),
      ),
    updateRecipe: (id: string, recipe: SourceBuildRecipe) =>
      action(
        () => updateSourceBuildRecipe(apiBaseUrl, id, recipe, scope.credential(), tenantId),
        (updated: SourceBuildConfiguration, guard) =>
          setRead(
            configurations.queryKey,
            configs.map((item) => (item.id === updated.id ? updated : item)),
            guard,
          ),
      ),
    reanalyze: (id: string) =>
      action(
        () => reanalyzeSourceRepository(apiBaseUrl, id, scope.credential(), tenantId),
        (updated: SourceRepository, guard) =>
          setRead(
            sources.queryKey,
            repositories.map((item) => (item.id === updated.id ? updated : item)),
            guard,
          ),
      ),
  }
}
export type SourceBuildsManager = ReturnType<typeof useSourceBuildsManager>
