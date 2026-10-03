import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createSourceBuildConfiguration, deleteSourceBuildConfiguration, fetchSourceBuildConfigurations,
  fetchSourceBuildJobs, fetchSourceRepositories, startSourceBuild, updateSourceBuildRecipe, reanalyzeSourceRepository } from '../api'
import type { LoadStatus, SourceBuildConfiguration, SourceBuildJob, SourceBuildRecipe, SourceRepository } from '../types'

interface UseSourceBuildsManagerOptions {
  apiBaseUrl: string; token?: string; tenantId?: string; webServiceId?: string | null; enabled: boolean
}
const terminalStatuses = new Set(['succeeded', 'failed'])
const messageOf = (value: unknown, fallback: string) => value instanceof Error ? value.message : fallback

export function useSourceBuildsManager({ apiBaseUrl, token, tenantId, webServiceId, enabled }: UseSourceBuildsManagerOptions) {
  const key = `${apiBaseUrl}:${token}:${tenantId}:${webServiceId}`
  const [scope, setScope] = useState(key)
  const [configurations, setConfigurations] = useState<SourceBuildConfiguration[]>([])
  const [sourceRepositories, setSourceRepositories] = useState<SourceRepository[]>([])
  const [jobs, setJobs] = useState<SourceBuildJob[]>([])
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [jobsStatus, setJobsStatus] = useState<LoadStatus>('idle')
  const [actionStatus, setActionStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [jobsError, setJobsError] = useState('')
  const [actionError, setActionError] = useState('')
  if (scope !== key) {
    setScope(key); setConfigurations([]); setSourceRepositories([]); setJobs([])
    setStatus('idle'); setJobsStatus('idle'); setActionStatus('idle'); setError(''); setJobsError(''); setActionError('')
  }
  const lifetime = useRef({ key, mounted: true })
  const configurationRead = useRef<AbortController | null>(null)
  const jobRead = useRef<AbortController | null>(null)
  useLayoutEffect(() => {
    lifetime.current = { key, mounted: true }
    return () => { lifetime.current.mounted = false; configurationRead.current?.abort(); jobRead.current?.abort() }
  }, [key])
  const current = useCallback((origin?: { key: string; mounted: boolean }) => lifetime.current.mounted && lifetime.current.key === key &&
    (!origin || origin === lifetime.current), [key])

  const refreshConfiguration = useCallback(async () => {
    configurationRead.current?.abort()
    const controller = new AbortController(); configurationRead.current = controller
    const origin = lifetime.current
    setStatus('loading'); setError('')
    try {
      const [configurations, sources] = await Promise.all([
        fetchSourceBuildConfigurations(apiBaseUrl, token, tenantId, controller.signal),
        fetchSourceRepositories(apiBaseUrl, token, tenantId, controller.signal),
      ])
      if (!current(origin) || controller.signal.aborted) return
      setConfigurations(configurations); setSourceRepositories(sources); setStatus('success')
    } catch (failure) {
      if (!current(origin) || controller.signal.aborted) return
      setStatus('error'); setError(messageOf(failure, 'Unable to load source build configuration.'))
    }
  }, [apiBaseUrl, token, tenantId, current])
  const refreshJobs = useCallback(async () => {
    if (!webServiceId) return
    jobRead.current?.abort()
    const controller = new AbortController(); jobRead.current = controller
    const origin = lifetime.current
    setJobsStatus('loading'); setJobsError('')
    try {
      const page = await fetchSourceBuildJobs(apiBaseUrl, webServiceId, token, tenantId, controller.signal)
      if (!current(origin) || controller.signal.aborted) return
      setJobs(page.items); setJobsStatus('success')
    } catch (failure) {
      if (!current(origin) || controller.signal.aborted) return
      setJobsStatus('error'); setJobsError(messageOf(failure, 'Unable to load build history.'))
    }
  }, [apiBaseUrl, token, tenantId, webServiceId, current])
  useEffect(() => {
    if (!enabled) return
    let active = true
    queueMicrotask(() => { if (active) { void refreshConfiguration(); void refreshJobs() } })
    return () => { active = false; configurationRead.current?.abort(); jobRead.current?.abort() }
  }, [enabled, refreshConfiguration, refreshJobs])
  const visibleJobs = jobs.filter((job) => job.webServiceId === webServiceId)
  const hasActiveBuild = visibleJobs.some((job) => !terminalStatuses.has(job.state.status))
  useEffect(() => {
    if (!enabled || !hasActiveBuild) return
    const timer = window.setInterval(() => void refreshJobs(), 2500)
    return () => window.clearInterval(timer)
  }, [enabled, hasActiveBuild, refreshJobs])

  const action = async <T,>(run: () => Promise<T>, accepted: (value: T) => void, fallback: string) => {
    const origin = lifetime.current
    setActionStatus('loading'); setActionError('')
    try {
      const value = await run()
      if (current(origin)) { accepted(value); setActionStatus('success') }
      return value
    } catch (failure) {
      if (current(origin)) { setActionStatus('error'); setActionError(messageOf(failure, fallback)) }
      throw failure
    }
  }
  return { configurations, sourceRepositories, jobs: visibleJobs, status, jobsStatus, actionStatus, error, jobsError,
    actionError, hasActiveBuild, refresh: () => Promise.all([refreshConfiguration(), refreshJobs()]),
    create: (sourceRepositoryId: string, targetWebServiceId: string) => action(
      () => createSourceBuildConfiguration(apiBaseUrl, sourceRepositoryId, targetWebServiceId, token, tenantId),
      (created) => { configurationRead.current?.abort(); setStatus('success'); setConfigurations((items) => [created, ...items.filter((item) => item.id !== created.id)]) }, 'Unable to configure source builds.'),
    remove: (configurationId: string) => action(
      () => deleteSourceBuildConfiguration(apiBaseUrl, configurationId, token, tenantId),
      () => { configurationRead.current?.abort(); setStatus('success'); setConfigurations((items) => items.filter((item) => item.id !== configurationId)) }, 'Unable to remove source configuration.'),
    start: (configurationId: string) => action(
      () => startSourceBuild(apiBaseUrl, configurationId, token, tenantId),
      (created) => { jobRead.current?.abort(); setJobsStatus('success'); setJobs((items) => [created, ...items.filter((item) => item.id !== created.id)]) }, 'Unable to queue a source build.'),
    updateRecipe: (configurationId: string, recipe: SourceBuildRecipe) => action(
      () => updateSourceBuildRecipe(apiBaseUrl, configurationId, recipe, token, tenantId),
      (updated) => { configurationRead.current?.abort(); setStatus('success'); setConfigurations((items) => items.map((item) => item.id === updated.id ? updated : item)) }, 'Unable to save the build recipe.'),
    reanalyze: (sourceRepositoryId: string) => action(
      () => reanalyzeSourceRepository(apiBaseUrl, sourceRepositoryId, token, tenantId),
      (updated) => { configurationRead.current?.abort(); setStatus('success'); setSourceRepositories((items) => items.map((item) => item.id === updated.id ? updated : item)) }, 'Unable to analyze the latest source commit.'),
  }
}
export type SourceBuildsManager = ReturnType<typeof useSourceBuildsManager>
