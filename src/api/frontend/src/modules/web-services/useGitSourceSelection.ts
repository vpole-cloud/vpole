import { useEffect, useLayoutEffect, useReducer, useRef, useState } from 'react'
import { useInfiniteQuery } from '@tanstack/react-query'
import {
  analyzeProviderRepository,
  fetchGitProviderConnections,
  fetchProviderBranches,
  fetchProviderRepositories,
  fetchSourceRepositories,
  linkProviderRepository,
} from '../../api'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { usePageVisible } from '../../realtime/usePageVisible'
import { useRealtime } from '../../realtime/context'
import { gitInterests, staleGitAnalysis } from '../platform/gitRealtime'
import {
  initialIntegrationSelection,
  integrationSelectionReducer,
} from '../platform/integrationsState'
import type { AppServices } from '../../shell/contracts'
import type { GitProviderPage, ProviderBranch, ProviderRepository } from '../../types'

function useGitPages<T>(
  api: AppServices['api'],
  key: readonly unknown[],
  enabled: boolean,
  active: boolean,
  load: (
    page: number,
    token: string | undefined,
    signal: AbortSignal,
  ) => Promise<GitProviderPage<T>>,
) {
  const scope = useApiScope(api)
  const visible = usePageVisible()
  const realtime = useRealtime()
  const allowed = realtime?.allowed(api.baseUrl, api.tenantId) ?? true
  const observing = enabled && allowed && active && visible
  const queryKey = scope.key(key)
  const identity = JSON.stringify(queryKey)
  const latest = useRef({ identity, load, observing })
  useLayoutEffect(() => {
    latest.current = { identity, load, observing }
  }, [identity, load, observing])
  const query = useInfiniteQuery(
    {
      queryKey,
      enabled: observing,
      initialPageParam: 1,
      refetchInterval: 60000,
      refetchIntervalInBackground: false,
      queryFn: async ({ pageParam, signal }) => {
        if (!scope.isCurrent() || latest.current.identity !== identity)
          throw new DOMException('Aborted', 'AbortError')
        const page = await latest.current.load(pageParam, scope.credential(), signal)
        if (!scope.isCurrent() || signal.aborted) throw new DOMException('Aborted', 'AbortError')
        return page
      },
      getNextPageParam: (page, _pages, pageNumber) => (page.hasMore ? pageNumber + 1 : undefined),
    },
    scope.session.client,
  )
  useEffect(() => {
    if (observing) return
    const queryKey = JSON.parse(identity)
    const cached = scope.session.client.getQueryCache().find({ queryKey, exact: true })
    if (cached && !cached.isActive())
      void scope.session.client.cancelQueries({ queryKey, exact: true })
  }, [observing, identity, scope.session])
  return {
    items: enabled && allowed ? (query.data?.pages.flatMap((page) => page.items) ?? []) : [],
    snapshot: query.data,
    status:
      !enabled || !allowed
        ? ('idle' as const)
        : query.isFetching || query.isPending
          ? ('loading' as const)
          : query.isError
            ? ('error' as const)
            : ('success' as const),
    error: query.error instanceof Error ? query.error.message : '',
    hasMore: query.hasNextPage,
    refresh: () => {
      if (
        observing &&
        latest.current.observing &&
        latest.current.identity === identity &&
        scope.isCurrent()
      )
        return query.refetch({ cancelRefetch: false })
    },
    more: () => {
      if (
        observing &&
        latest.current.observing &&
        latest.current.identity === identity &&
        scope.isCurrent() &&
        !query.isFetching &&
        query.hasNextPage
      )
        void query.fetchNextPage({ cancelRefetch: false })
    },
  }
}

export function useGitSourceSelection(api: AppServices['api'], allowed = true, active = true) {
  const scope = useApiScope(api)
  const queryScope = [api.baseUrl, api.tenantId]
  const [selection, dispatch] = useReducer(integrationSelectionReducer, initialIntegrationSelection)
  const connections = useScopedQuery({
    scope: queryScope,
    key: ['git', 'connections'],
    active,
    realtime: gitInterests(api.tenantId, ['git', 'connections']),
    load: (signal) =>
      fetchGitProviderConnections(api.baseUrl, scope.credential(), api.tenantId, signal),
  })
  const sources = useScopedQuery({
    scope: queryScope,
    key: ['git', 'sources'],
    active,
    realtime: gitInterests(api.tenantId, ['git', 'sources']),
    load: (signal) =>
      fetchSourceRepositories(api.baseUrl, scope.credential(), api.tenantId, signal),
  })
  const connectionItems = connections.resource
  useEffect(() => {
    if (connectionItems)
      dispatch({ type: 'connectionsLoaded', connectionIds: connectionItems.map((item) => item.id) })
  }, [connectionItems])
  const repositories = useGitPages<ProviderRepository>(
    api,
    ['git', 'repositories', selection.connectionId],
    Boolean(selection.connectionId),
    active,
    (page, token, signal) =>
      fetchProviderRepositories(
        api.baseUrl,
        selection.connectionId!,
        token,
        api.tenantId,
        signal,
        page,
      ),
  )
  const branches = useGitPages<ProviderBranch>(
    api,
    ['git', 'branches', selection.connectionId, selection.repositoryId],
    Boolean(selection.connectionId && selection.repositoryId),
    active,
    (page, token, signal) =>
      fetchProviderBranches(
        api.baseUrl,
        selection.connectionId!,
        selection.repositoryId!,
        token,
        api.tenantId,
        signal,
        page,
      ),
  )
  const identity = `${scope.identity}:${selection.connectionId}:${selection.repositoryId}:${selection.branch}:${selection.candidateKey}`
  const mutation = useScopedMutation(`${identity}:${allowed}:${active}`)
  const [analysisEvidence, setAnalysisEvidence] = useState<{
    analysis: typeof selection.analysis
    snapshot: unknown
  } | null>(null)
  const analysisStale = staleGitAnalysis(
    selection.analysis,
    branches.items,
    branches.hasMore === false,
    selection.analysis === analysisEvidence?.analysis &&
      branches.snapshot !== analysisEvidence?.snapshot,
  )
  const analyze = async () => {
    if (
      !allowed ||
      !active ||
      !selection.connectionId ||
      !selection.repositoryId ||
      !selection.branch
    )
      return
    await mutation
      .run(
        (signal) =>
          analyzeProviderRepository(
            api.baseUrl,
            selection.connectionId!,
            selection.repositoryId!,
            selection.branch,
            scope.credential(),
            api.tenantId,
            signal,
          ),
        (analysis) => {
          setAnalysisEvidence({ analysis, snapshot: branches.snapshot })
          dispatch({ type: 'analysisLoaded', analysis })
        },
      )
      .catch(() => {})
  }
  const link = async (technicalName: string) => {
    if (
      !allowed ||
      !active ||
      !selection.connectionId ||
      !selection.repositoryId ||
      !selection.branch ||
      !technicalName ||
      !scope.isCurrent()
    )
      return
    const existing = sources.resource?.find(
      (source) =>
        source.connectionId === selection.connectionId &&
        source.externalRepositoryId === selection.repositoryId &&
        source.branch === selection.branch &&
        source.technicalName === technicalName,
    )
    if (existing) return existing
    return mutation
      .run(async (signal) => {
        const source = await linkProviderRepository(
          api.baseUrl,
          selection.connectionId!,
          selection.repositoryId!,
          selection.branch,
          technicalName,
          scope.credential(),
          api.tenantId,
          signal,
        )
        if (signal.aborted || !scope.isCurrent()) throw new DOMException('Aborted', 'AbortError')
        await scope.session.client.cancelQueries({ queryKey: sources.queryKey, exact: true })
        if (signal.aborted || !scope.isCurrent()) throw new DOMException('Aborted', 'AbortError')
        scope.session.client.setQueryData(sources.queryKey, {
          identity: JSON.stringify(sources.queryKey),
          value: [source, ...(sources.resource ?? []).filter((item) => item.id !== source.id)],
        })
        void scope.session.client.invalidateQueries({
          queryKey: scope.key(['git', 'sources']),
          refetchType: 'active',
        })
        return source
      })
      .catch(() => undefined)
  }
  const readError = connections.error || sources.error || repositories.error || branches.error
  return {
    selection,
    connections: connections.resource ?? [],
    sources: sources.resource ?? [],
    repositories: repositories.items,
    branches: branches.items,
    status:
      connections.status === 'error' || sources.status === 'error'
        ? ('error' as const)
        : connections.status === 'loading' || sources.status === 'loading'
          ? ('loading' as const)
          : ('success' as const),
    repositoryStatus: repositories.status,
    branchStatus: branches.status,
    analysisStale,
    sourcesCurrent: sources.resource !== null,
    busy: mutation.busy,
    error: mutation.error instanceof Error ? mutation.error.message : readError,
    moreRepositories: repositories.hasMore,
    moreBranches: branches.hasMore,
    analyze,
    link,
    refresh: () => {
      void connections.refresh()
      void sources.refresh()
      void repositories.refresh()
      void branches.refresh()
    },
    loadMoreRepositories: repositories.more,
    loadMoreBranches: branches.more,
    selectConnection: (connectionId: string) => {
      mutation.clearError()
      dispatch({ type: 'selectConnection', connectionId })
    },
    selectRepository: (repositoryId: string) => {
      mutation.clearError()
      dispatch({
        type: 'selectRepository',
        repositoryId,
        defaultBranch:
          repositories.items.find((item) => item.id === repositoryId)?.defaultBranch ?? '',
      })
    },
    selectBranch: (branch: string) => {
      mutation.clearError()
      dispatch({ type: 'selectBranch', branch })
    },
    selectCandidate: (candidateKey: string) => {
      mutation.clearError()
      dispatch({ type: 'selectCandidate', candidateKey })
    },
  }
}
