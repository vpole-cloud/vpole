import { useEffect, useLayoutEffect, useReducer, useRef, useState } from 'react'
import { analyzeProviderRepository, fetchGitProviderConnections, fetchProviderBranches, fetchProviderRepositories,
  fetchSourceRepositories, linkProviderRepository, reanalyzeSourceRepository } from '../../api'
import { initialIntegrationSelection, integrationSelectionReducer } from '../../integrationsState'
import type { AppServices } from '../../shell/contracts'
import type { GitProviderConnection, LoadStatus, ProviderBranch, ProviderRepository, SourceRepository } from '../../types'

const message = (error: unknown) => error instanceof Error ? error.message : 'Unable to load Git source.'
const unique = <T extends { id: string }>(items: T[]) => [...new Map(items.map((item) => [item.id, item])).values()]

export function useGitSourceSelection(api: AppServices['api']) {
  const apiKey = `${api.baseUrl}:${api.tenantId}:${api.token}`
  const [scope, setScope] = useState(apiKey)
  const [selection, dispatch] = useReducer(integrationSelectionReducer, initialIntegrationSelection)
  const [connections, setConnections] = useState<GitProviderConnection[]>([])
  const [sources, setSources] = useState<SourceRepository[]>([])
  const [repositories, setRepositories] = useState<ProviderRepository[]>([])
  const [branches, setBranches] = useState<ProviderBranch[]>([])
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [repositoryStatus, setRepositoryStatus] = useState<LoadStatus>('idle')
  const [branchStatus, setBranchStatus] = useState<LoadStatus>('idle')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [repositoryPage, setRepositoryPage] = useState(1)
  const [branchPage, setBranchPage] = useState(1)
  const [moreRepositories, setMoreRepositories] = useState(false)
  const [moreBranches, setMoreBranches] = useState(false)
  const [refresh, setRefresh] = useState(0)
  if (scope !== apiKey) {
    setScope(apiKey); setConnections([]); setSources([]); setRepositories([]); setBranches([])
    setStatus('idle'); setRepositoryStatus('idle'); setBranchStatus('idle'); setBusy(false); setError('')
    setRepositoryPage(1); setBranchPage(1); setMoreRepositories(false); setMoreBranches(false)
    dispatch({ type: 'connectionsLoaded', connectionIds: [] })
  }
  const selectionKey = `${api.baseUrl}:${api.tenantId}:${api.token}:${selection.connectionId}:${selection.repositoryId}:${selection.branch}`
  const current = useRef({ key: selectionKey, mounted: true })
  useLayoutEffect(() => { current.current = { key: selectionKey, mounted: true }; return () => { current.current.mounted = false } }, [selectionKey])

  useEffect(() => {
    const controller = new AbortController()
    void (async () => {
      setStatus('loading'); setError('')
      try {
        const [connections, sources] = await Promise.all([
          fetchGitProviderConnections(api.baseUrl, api.token, api.tenantId, controller.signal),
          fetchSourceRepositories(api.baseUrl, api.token, api.tenantId, controller.signal),
        ])
        if (controller.signal.aborted) return
        setConnections(connections); setSources(sources); setStatus('success')
        dispatch({ type: 'connectionsLoaded', connectionIds: connections.map((item) => item.id) })
      } catch (failure) { if (!controller.signal.aborted) { setError(message(failure)); setStatus('error') } }
    })()
    return () => controller.abort()
  }, [api.baseUrl, api.token, api.tenantId, refresh])

  useEffect(() => {
    if (!selection.connectionId) return
    const controller = new AbortController()
    void (async () => {
      setRepositoryStatus('loading'); setError('')
      try {
        const page = await fetchProviderRepositories(api.baseUrl, selection.connectionId!, api.token, api.tenantId, controller.signal, repositoryPage)
        if (controller.signal.aborted) return
        setRepositories((items) => unique(repositoryPage === 1 ? page.items : [...items, ...page.items]))
        setMoreRepositories(page.hasMore); setRepositoryStatus('success')
      } catch (failure) { if (!controller.signal.aborted) { setError(message(failure)); setRepositoryStatus('error') } }
    })()
    return () => controller.abort()
  }, [api.baseUrl, api.token, api.tenantId, selection.connectionId, repositoryPage, refresh])

  useEffect(() => {
    if (!selection.connectionId || !selection.repositoryId) return
    const controller = new AbortController()
    void (async () => {
      setBranchStatus('loading'); setError('')
      try {
        const page = await fetchProviderBranches(api.baseUrl, selection.connectionId!, selection.repositoryId!, api.token, api.tenantId, controller.signal, branchPage)
        if (controller.signal.aborted) return
        setBranches((items) => [...new Map((branchPage === 1 ? page.items : [...items, ...page.items]).map((item) => [item.name, item])).values()])
        setMoreBranches(page.hasMore); setBranchStatus('success')
      } catch (failure) { if (!controller.signal.aborted) { setError(message(failure)); setBranchStatus('error') } }
    })()
    return () => controller.abort()
  }, [api.baseUrl, api.token, api.tenantId, selection.connectionId, selection.repositoryId, branchPage, refresh])

  const analyze = async () => {
    if (!selection.connectionId || !selection.repositoryId || !selection.branch) return
    setBusy(true); setError('')
    const origin = current.current
    const valid = () => origin === current.current && origin.mounted
    try {
      const analysis = await analyzeProviderRepository(api.baseUrl, selection.connectionId, selection.repositoryId, selection.branch, api.token, api.tenantId)
      if (valid()) dispatch({ type: 'analysisLoaded', analysis })
    } catch (failure) { if (valid()) setError(message(failure)) }
    finally { if (valid()) setBusy(false) }
  }
  const link = async () => {
    if (!selection.connectionId || !selection.repositoryId || !selection.branch || !selection.candidateKey) return
    const existing = sources.find((source) => source.connectionId === selection.connectionId &&
      source.externalRepositoryId === selection.repositoryId && source.branch === selection.branch && source.selectedPlan.candidateKey === selection.candidateKey)
    if (existing && existing.commitSha === selection.analysis?.commitSha) return existing
    setBusy(true); setError('')
    const origin = current.current
    const valid = () => origin === current.current && origin.mounted
    try {
      const source = existing ? await reanalyzeSourceRepository(api.baseUrl, existing.id, api.token, api.tenantId) :
        await linkProviderRepository(api.baseUrl, selection.connectionId, selection.repositoryId, selection.branch, selection.candidateKey, api.token, api.tenantId)
      if (!valid()) return
      setSources((items) => [source, ...items.filter((item) => item.id !== source.id)])
      return source
    } catch (failure) { if (valid()) setError(message(failure)) }
    finally { if (valid()) setBusy(false) }
  }
  return { selection, connections, sources, repositories, branches, status, repositoryStatus, branchStatus, busy, error,
    moreRepositories, moreBranches, analyze, link, refresh: () => setRefresh((value) => value + 1),
    loadMoreRepositories: () => setRepositoryPage((value) => value + 1), loadMoreBranches: () => setBranchPage((value) => value + 1),
    selectConnection: (connectionId: string) => {
      setRepositoryPage(1); setBranchPage(1); setRepositories([]); setBranches([]); setError(''); setBusy(false)
      dispatch({ type: 'selectConnection', connectionId })
    },
    selectRepository: (repositoryId: string) => {
      setBranchPage(1); setBranches([]); setError(''); setBusy(false)
      dispatch({ type: 'selectRepository', repositoryId, defaultBranch: repositories.find((item) => item.id === repositoryId)?.defaultBranch ?? '' })
    },
    selectBranch: (branch: string) => { setError(''); setBusy(false); dispatch({ type: 'selectBranch', branch }) },
    selectCandidate: (candidateKey: string) => dispatch({ type: 'selectCandidate', candidateKey }),
  }
}
