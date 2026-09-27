import { useEffect, useReducer, useState } from 'react'
import {
  analyzeProviderRepository,
  beginGitHubInstallation,
  disconnectGitProviderConnection,
  fetchGitProviderConnections,
  fetchProviderBranches,
  fetchProviderRepositories,
  fetchSourceRepositories,
  linkProviderRepository,
  reanalyzeSourceRepository,
  unlinkSourceRepository,
} from '../api'
import {
  initialIntegrationSelection,
  integrationSelectionReducer,
} from '../integrationsState'
import type {
  GitProviderConnection,
  LoadStatus,
  ProviderBranch,
  ProviderRepository,
  SourceRepository,
} from '../types'

interface UseGitIntegrationsManagerOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
  preferredConnectionId?: string
}

const errorMessage = (value: unknown, fallback: string) =>
  value instanceof Error ? value.message : fallback

export function useGitIntegrationsManager({
  apiBaseUrl,
  token,
  tenantId,
  enabled,
  preferredConnectionId,
}: UseGitIntegrationsManagerOptions) {
  const [connections, setConnections] = useState<GitProviderConnection[]>([])
  const [sourceRepositories, setSourceRepositories] = useState<SourceRepository[]>([])
  const [repositories, setRepositories] = useState<ProviderRepository[]>([])
  const [branches, setBranches] = useState<ProviderBranch[]>([])
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [repositoryStatus, setRepositoryStatus] = useState<LoadStatus>('idle')
  const [branchStatus, setBranchStatus] = useState<LoadStatus>('idle')
  const [actionStatus, setActionStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [actionError, setActionError] = useState('')
  const [busySourceRepositoryId, setBusySourceRepositoryId] = useState<string | null>(null)
  const [selection, dispatch] = useReducer(
    integrationSelectionReducer,
    initialIntegrationSelection,
  )

  useEffect(() => {
    if (!enabled) {
      return
    }

    const controller = new AbortController()
    const load = async () => {
      setStatus('loading')
      setError('')
      try {
        const [nextConnections, nextSources] = await Promise.all([
          fetchGitProviderConnections(apiBaseUrl, token, tenantId, controller.signal),
          fetchSourceRepositories(apiBaseUrl, token, tenantId, controller.signal),
        ])
        setConnections(nextConnections)
        setSourceRepositories(nextSources)
        const connectionIds = nextConnections.map((connection) => connection.id)
        if (preferredConnectionId && connectionIds.includes(preferredConnectionId)) {
          connectionIds.splice(connectionIds.indexOf(preferredConnectionId), 1)
          connectionIds.unshift(preferredConnectionId)
        }
        dispatch({ type: 'connectionsLoaded', connectionIds })
        setStatus('success')
      } catch (loadError) {
        if (controller.signal.aborted) {
          return
        }
        setConnections([])
        setSourceRepositories([])
        dispatch({ type: 'connectionsLoaded', connectionIds: [] })
        setStatus('error')
        setError(errorMessage(loadError, 'Unable to load integrations.'))
      }
    }

    void load()
    return () => controller.abort()
  }, [apiBaseUrl, enabled, preferredConnectionId, tenantId, token])

  useEffect(() => {
    if (!enabled || !selection.connectionId) {
      let active = true
      queueMicrotask(() => {
        if (!active) return
        setRepositories([])
        setRepositoryStatus('idle')
      })
      return () => {
        active = false
      }
    }

    const controller = new AbortController()
    const load = async () => {
      setRepositoryStatus('loading')
      setError('')
      try {
        const page = await fetchProviderRepositories(
          apiBaseUrl,
          selection.connectionId!,
          token,
          tenantId,
          controller.signal,
        )
        setRepositories(page.items)
        setRepositoryStatus('success')
      } catch (loadError) {
        if (controller.signal.aborted) {
          return
        }
        setRepositories([])
        setRepositoryStatus('error')
        setError(errorMessage(loadError, 'Unable to load GitHub repositories.'))
      }
    }

    void load()
    return () => controller.abort()
  }, [apiBaseUrl, enabled, selection.connectionId, tenantId, token])

  useEffect(() => {
    if (!enabled || !selection.connectionId || !selection.repositoryId) {
      let active = true
      queueMicrotask(() => {
        if (!active) return
        setBranches([])
        setBranchStatus('idle')
      })
      return () => {
        active = false
      }
    }

    const controller = new AbortController()
    const load = async () => {
      setBranchStatus('loading')
      setError('')
      try {
        const page = await fetchProviderBranches(
          apiBaseUrl,
          selection.connectionId!,
          selection.repositoryId!,
          token,
          tenantId,
          controller.signal,
        )
        setBranches(page.items)
        setBranchStatus('success')
      } catch (loadError) {
        if (controller.signal.aborted) {
          return
        }
        setBranches([])
        setBranchStatus('error')
        setError(errorMessage(loadError, 'Unable to load repository branches.'))
      }
    }

    void load()
    return () => controller.abort()
  }, [
    apiBaseUrl,
    enabled,
    selection.connectionId,
    selection.repositoryId,
    tenantId,
    token,
  ])

  const refresh = async () => {
    setStatus('loading')
    setError('')
    try {
      const [nextConnections, nextSources] = await Promise.all([
        fetchGitProviderConnections(apiBaseUrl, token, tenantId),
        fetchSourceRepositories(apiBaseUrl, token, tenantId),
      ])
      setConnections(nextConnections)
      setSourceRepositories(nextSources)
      dispatch({
        type: 'connectionsLoaded',
        connectionIds: nextConnections.map((connection) => connection.id),
      })
      setStatus('success')
    } catch (loadError) {
      setStatus('error')
      setError(errorMessage(loadError, 'Unable to refresh integrations.'))
    }
  }

  const connectGitHub = async () => {
    setActionStatus('loading')
    setActionError('')
    try {
      const installation = await beginGitHubInstallation(apiBaseUrl, token, tenantId)
      window.location.assign(installation.installUrl)
    } catch (actionErrorValue) {
      setActionStatus('error')
      setActionError(errorMessage(actionErrorValue, 'Unable to start GitHub installation.'))
    }
  }

  const selectConnection = (connectionId: string) =>
    dispatch({ type: 'selectConnection', connectionId })

  const selectRepository = (repositoryId: string) => {
    const repository = repositories.find((item) => item.id === repositoryId)
    dispatch({
      type: 'selectRepository',
      repositoryId: repository?.id ?? null,
      defaultBranch: repository?.defaultBranch ?? '',
    })
  }

  const selectBranch = (branch: string) => dispatch({ type: 'selectBranch', branch })

  const analyze = async () => {
    if (!selection.connectionId || !selection.repositoryId || !selection.branch) {
      return
    }

    setActionStatus('loading')
    setActionError('')
    try {
      const analysis = await analyzeProviderRepository(
        apiBaseUrl,
        selection.connectionId,
        selection.repositoryId,
        selection.branch,
        token,
        tenantId,
      )
      dispatch({ type: 'analysisLoaded', analysis })
      setActionStatus('success')
    } catch (actionErrorValue) {
      setActionStatus('error')
      setActionError(errorMessage(actionErrorValue, 'Unable to analyze the repository.'))
    }
  }

  const link = async () => {
    if (
      !selection.connectionId ||
      !selection.repositoryId ||
      !selection.branch ||
      !selection.candidateKey
    ) {
      return
    }

    setActionStatus('loading')
    setActionError('')
    try {
      const linked = await linkProviderRepository(
        apiBaseUrl,
        selection.connectionId,
        selection.repositoryId,
        selection.branch,
        selection.candidateKey,
        token,
        tenantId,
      )
      setSourceRepositories((current) => [linked, ...current])
      setActionStatus('success')
    } catch (actionErrorValue) {
      setActionStatus('error')
      setActionError(errorMessage(actionErrorValue, 'Unable to link the repository.'))
    }
  }

  const reanalyze = async (sourceRepositoryId: string) => {
    setBusySourceRepositoryId(sourceRepositoryId)
    setActionError('')
    try {
      const updated = await reanalyzeSourceRepository(
        apiBaseUrl,
        sourceRepositoryId,
        token,
        tenantId,
      )
      setSourceRepositories((current) =>
        current.map((item) => (item.id === updated.id ? updated : item)),
      )
    } catch (actionErrorValue) {
      setActionError(errorMessage(actionErrorValue, 'Unable to reanalyze the repository.'))
    } finally {
      setBusySourceRepositoryId(null)
    }
  }

  const unlink = async (sourceRepositoryId: string) => {
    setBusySourceRepositoryId(sourceRepositoryId)
    setActionError('')
    try {
      await unlinkSourceRepository(apiBaseUrl, sourceRepositoryId, token, tenantId)
      setSourceRepositories((current) =>
        current.filter((item) => item.id !== sourceRepositoryId),
      )
    } catch (actionErrorValue) {
      setActionError(errorMessage(actionErrorValue, 'Unable to unlink the repository.'))
    } finally {
      setBusySourceRepositoryId(null)
    }
  }

  const disconnect = async (connectionId: string) => {
    setActionStatus('loading')
    setActionError('')
    try {
      await disconnectGitProviderConnection(apiBaseUrl, connectionId, token, tenantId)
      const remaining = connections.filter((connection) => connection.id !== connectionId)
      setConnections(remaining)
      dispatch({
        type: 'connectionsLoaded',
        connectionIds: remaining.map((connection) => connection.id),
      })
      setActionStatus('success')
    } catch (actionErrorValue) {
      setActionStatus('error')
      setActionError(errorMessage(actionErrorValue, 'Unable to disconnect GitHub.'))
    }
  }

  return {
    connections,
    sourceRepositories,
    repositories,
    branches,
    selection,
    status,
    repositoryStatus,
    branchStatus,
    actionStatus,
    error,
    actionError,
    busySourceRepositoryId,
    refresh,
    connectGitHub,
    selectConnection,
    selectRepository,
    selectBranch,
    selectCandidate: (candidateKey: string) =>
      dispatch({ type: 'selectCandidate', candidateKey }),
    analyze,
    link,
    reanalyze,
    unlink,
    disconnect,
  }
}

export type GitIntegrationsManager = ReturnType<typeof useGitIntegrationsManager>
