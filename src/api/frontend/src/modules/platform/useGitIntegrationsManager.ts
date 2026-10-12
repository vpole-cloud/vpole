import { useEffect, useLayoutEffect, useReducer, useRef, useState } from 'react'
import { analyzeProviderRepository, beginGitHubInstallation, disconnectGitProviderConnection, fetchGitProviderConnections, fetchProviderBranches, fetchProviderRepositories, fetchSourceRepositories, linkProviderRepository, reanalyzeSourceRepository, unlinkSourceRepository } from '../../api'
import { initialIntegrationSelection, integrationSelectionReducer, type IntegrationSelectionAction } from './integrationsState'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import type { LoadStatus } from '../../types'
import { gitInterests, staleGitAnalysis } from './gitRealtime'

interface UseGitIntegrationsManagerOptions { apiBaseUrl: string; token?: string; tenantId?: string; enabled: boolean; canManage?: boolean; preferredConnectionId?: string }
export function useGitIntegrationsManager({ apiBaseUrl, token, tenantId, enabled, canManage = true, preferredConnectionId }: UseGitIntegrationsManagerOptions) {
  const origin = useApiScope({ baseUrl: apiBaseUrl, token, tenantId })
  const identity = `${origin.identity}:${enabled}:${canManage}`
  const permission = useRef({ enabled, canManage })
  useLayoutEffect(() => { permission.current = { enabled, canManage } }, [enabled, canManage])
  const current = () => origin.isCurrent() && permission.current.enabled === enabled && permission.current.canManage === canManage
  const [ownedSelection, dispatchOwned] = useReducer((state: { identity: string; value: typeof initialIntegrationSelection }, command: { identity: string; action: IntegrationSelectionAction }) => ({
    identity: command.identity,
    value: integrationSelectionReducer(state.identity === command.identity ? state.value : initialIntegrationSelection, command.action),
  }), { identity, value: initialIntegrationSelection })
  const selection = ownedSelection.identity === identity ? ownedSelection.value : initialIntegrationSelection
  const dispatch = (action: IntegrationSelectionAction) => dispatchOwned({ identity, action })
  const [pages, setPages] = useState({ identity, connection: '', repository: '', repositories: 1, branches: 1 })
  const repositoryPage = pages.identity === identity && pages.connection === selection.connectionId ? pages.repositories : 1
  const branchPage = pages.identity === identity && pages.connection === selection.connectionId && pages.repository === selection.repositoryId ? pages.branches : 1
  const connections = useScopedQuery({ scope: [apiBaseUrl, tenantId], key: ['git', 'connections'], enabled,
    realtime: gitInterests(tenantId, ['git', 'connections']),
    load: (signal) => fetchGitProviderConnections(apiBaseUrl, origin.credential(), tenantId, signal) })
  const sources = useScopedQuery({ scope: [apiBaseUrl, tenantId], key: ['git', 'sources'], enabled,
    realtime: gitInterests(tenantId, ['git', 'sources']),
    load: (signal) => fetchSourceRepositories(apiBaseUrl, origin.credential(), tenantId, signal) })
  const repositoryQuery = useScopedQuery({ scope: [apiBaseUrl, tenantId], key: ['git', 'repositories', selection.connectionId, repositoryPage], enabled: enabled && Boolean(selection.connectionId),
    realtime: [], interval: 60000,
    load: (signal) => fetchProviderRepositories(apiBaseUrl, selection.connectionId!, origin.credential(), tenantId, signal, repositoryPage) })
  const branchQuery = useScopedQuery({ scope: [apiBaseUrl, tenantId], key: ['git', 'branches', selection.connectionId, selection.repositoryId, branchPage], enabled: enabled && Boolean(selection.connectionId && selection.repositoryId),
    realtime: [], interval: 60000,
    load: (signal) => fetchProviderBranches(apiBaseUrl, selection.connectionId!, selection.repositoryId!, origin.credential(), tenantId, signal, branchPage) })
  const loadedIds = JSON.stringify(connections.resource?.map((connection) => connection.id) ?? [])
  useEffect(() => {
    if (!enabled || connections.status !== 'success') return
    const connectionIds: string[] = JSON.parse(loadedIds)
    if (preferredConnectionId && connectionIds.includes(preferredConnectionId)) {
      connectionIds.splice(connectionIds.indexOf(preferredConnectionId), 1); connectionIds.unshift(preferredConnectionId)
    }
    dispatchOwned({ identity, action: { type: 'connectionsLoaded', connectionIds } })
  }, [enabled, connections.status, loadedIds, preferredConnectionId, identity])
  const [accepted, setAccepted] = useState<string | null>(null)
  const [busySource, setBusySource] = useState<{ identity: string; id: string } | null>(null)
  const mutation = useScopedMutation(identity)
  const analysisIdentity = `${identity}:${selection.connectionId}:${selection.repositoryId}:${selection.branch}`
  const analysisMutation = useScopedMutation(analysisIdentity)
  const [analysisEvidence, setAnalysisEvidence] = useState<{ analysis: typeof selection.analysis; snapshot: unknown } | null>(null)
  const analysisStale = staleGitAnalysis(selection.analysis, branchQuery.resource?.items ?? [], branchPage === 1 && branchQuery.resource?.hasMore === false,
    selection.analysis === analysisEvidence?.analysis && branchQuery.resource !== analysisEvidence?.snapshot)
  const invalidate = async () => {
    await origin.session.client.invalidateQueries({ queryKey: origin.key(['git']), refetchType: 'active' })
    if (!current()) return
    await Promise.all(['overview', 'search'].map((prefix) => origin.session.client.invalidateQueries({ queryKey: origin.key([prefix]), refetchType: 'active' })))
  }
  const refresh = async () => { await Promise.all([connections.refresh(), sources.refresh(), repositoryQuery.refresh(), branchQuery.refresh()]) }
  const write = async (action: (signal: AbortSignal) => Promise<unknown>, sourceId?: string) => {
    if (!enabled || !canManage || !current()) return
    setAccepted(null)
    if (sourceId) setBusySource({ identity, id: sourceId })
    try {
      await mutation.run(action, async () => { setAccepted(identity); await invalidate() })
    } catch { /* Scoped errors are rendered by the manager; no unhandled UI promises. */ }
    finally { if (current()) setBusySource(null) }
  }
  const connectGitHub = async () => {
    if (!enabled || !canManage || !current()) return
    try { await mutation.run((signal) => beginGitHubInstallation(apiBaseUrl, origin.credential(), tenantId, signal), (installation) => { if (current()) window.location.assign(installation.installUrl) }) }
    catch { /* Scoped error is displayed. */ }
  }
  const analyze = async () => {
    if (!enabled || !canManage || !selection.connectionId || !selection.repositoryId || !selection.branch) return
    const command = { connection: selection.connectionId, repository: selection.repositoryId, branch: selection.branch }
    try {
      await analysisMutation.run((signal) => analyzeProviderRepository(apiBaseUrl, command.connection, command.repository, command.branch, origin.credential(), tenantId, signal), (analysis) => {
        if (current()) { setAnalysisEvidence({ analysis, snapshot: branchQuery.resource }); dispatch({ type: 'analysisLoaded', analysis }) }
      })
    } catch { /* Scoped error is displayed. */ }
  }
  const link = async () => {
    if (analysisStale || !selection.connectionId || !selection.repositoryId || !selection.branch || !selection.candidateKey) return
    const command = { ...selection }
    await write((signal) => linkProviderRepository(apiBaseUrl, command.connectionId!, command.repositoryId!, command.branch, command.candidateKey!, origin.credential(), tenantId, signal))
  }
  const selectConnection = (connectionId: string) => dispatch({ type: 'selectConnection', connectionId })
  const selectRepository = (repositoryId: string) => {
    const repository = repositoryQuery.resource?.items.find((item) => item.id === repositoryId)
    dispatch({ type: 'selectRepository', repositoryId: repository?.id ?? null, defaultBranch: repository?.defaultBranch ?? '' })
  }
  const selectRepositoryPage = (page: number) => {
    setPages({ identity, connection: selection.connectionId ?? '', repository: '', repositories: page, branches: 1 })
    dispatch({ type: 'selectRepository', repositoryId: null, defaultBranch: '' })
  }
  const selectBranchPage = (page: number) => setPages({ identity, connection: selection.connectionId ?? '', repository: selection.repositoryId ?? '', repositories: repositoryPage, branches: page })
  const error = connections.error || sources.error || repositoryQuery.error || branchQuery.error
  const actionError = analysisMutation.error ?? mutation.error
  return {
    connections: connections.resource ?? [], sourceRepositories: sources.resource ?? [], repositories: repositoryQuery.resource?.items ?? [], branches: branchQuery.resource?.items ?? [], selection,
    status: (connections.status !== 'success' ? connections.status : sources.status) as LoadStatus,
    repositoryStatus: repositoryQuery.status, branchStatus: branchQuery.status,
    actionStatus: (mutation.busy || analysisMutation.busy ? 'loading' : actionError ? 'error' : accepted === identity || selection.analysis ? 'success' : 'idle') as LoadStatus,
    error, analysisStale, actionError: actionError instanceof Error ? actionError.message : '', busySourceRepositoryId: busySource?.identity === identity ? busySource.id : null,
    repositoryPage, branchPage, hasMoreRepositories: repositoryQuery.resource?.hasMore ?? false, hasMoreBranches: branchQuery.resource?.hasMore ?? false,
    selectRepositoryPage, selectBranchPage, refresh, connectGitHub, selectConnection, selectRepository,
    selectBranch: (branch: string) => dispatch({ type: 'selectBranch', branch }), selectCandidate: (candidateKey: string) => dispatch({ type: 'selectCandidate', candidateKey }), analyze, link,
    reanalyze: (id: string) => write((signal) => reanalyzeSourceRepository(apiBaseUrl, id, origin.credential(), tenantId, signal), id),
    unlink: (id: string) => write((signal) => unlinkSourceRepository(apiBaseUrl, id, origin.credential(), tenantId, signal), id),
    disconnect: (id: string) => write((signal) => disconnectGitProviderConnection(apiBaseUrl, id, origin.credential(), tenantId, signal)),
  }
}
export type GitIntegrationsManager = ReturnType<typeof useGitIntegrationsManager>
