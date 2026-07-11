import { useCallback, useEffect, useState } from 'react'
import {
  createSourceBuildConfiguration,
  deleteSourceBuildConfiguration,
  fetchSourceBuildConfigurations,
  fetchSourceBuildJobs,
  fetchSourceRepositories,
  startSourceBuild,
} from '../api'
import type {
  LoadStatus,
  SourceBuildConfiguration,
  SourceBuildJob,
  SourceRepository,
} from '../types'

interface UseSourceBuildsManagerOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  webServiceId?: string | null
  enabled: boolean
}

const terminalStatuses = new Set(['succeeded', 'failed'])

const messageOf = (value: unknown, fallback: string) =>
  value instanceof Error ? value.message : fallback

export function useSourceBuildsManager({
  apiBaseUrl,
  token,
  tenantId,
  webServiceId,
  enabled,
}: UseSourceBuildsManagerOptions) {
  const [configurations, setConfigurations] = useState<SourceBuildConfiguration[]>([])
  const [sourceRepositories, setSourceRepositories] = useState<SourceRepository[]>([])
  const [jobs, setJobs] = useState<SourceBuildJob[]>([])
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [jobsStatus, setJobsStatus] = useState<LoadStatus>('idle')
  const [actionStatus, setActionStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [actionError, setActionError] = useState('')

  const refreshConfiguration = useCallback(async () => {
    const [nextConfigurations, nextSources] = await Promise.all([
      fetchSourceBuildConfigurations(apiBaseUrl, token, tenantId),
      fetchSourceRepositories(apiBaseUrl, token, tenantId),
    ])
    setConfigurations(nextConfigurations)
    setSourceRepositories(nextSources)
  }, [apiBaseUrl, tenantId, token])

  const refreshJobs = useCallback(async () => {
    if (!webServiceId) {
      setJobs([])
      setJobsStatus('idle')
      return
    }
    setJobsStatus('loading')
    try {
      const page = await fetchSourceBuildJobs(
        apiBaseUrl,
        webServiceId,
        token,
        tenantId,
      )
      setJobs(page.items)
      setJobsStatus('success')
    } catch (loadError) {
      setJobsStatus('error')
      setError(messageOf(loadError, 'Unable to load source build history.'))
    }
  }, [apiBaseUrl, tenantId, token, webServiceId])

  useEffect(() => {
    if (!enabled) {
      return
    }
    const controller = new AbortController()
    const load = async () => {
      setStatus('loading')
      setError('')
      try {
        const [nextConfigurations, nextSources] = await Promise.all([
          fetchSourceBuildConfigurations(apiBaseUrl, token, tenantId, controller.signal),
          fetchSourceRepositories(apiBaseUrl, token, tenantId, controller.signal),
        ])
        if (controller.signal.aborted) return
        setConfigurations(nextConfigurations)
        setSourceRepositories(nextSources)
        setStatus('success')
      } catch (loadError) {
        if (controller.signal.aborted) return
        setStatus('error')
        setError(messageOf(loadError, 'Unable to load source build configuration.'))
      }
    }
    void load()
    return () => controller.abort()
  }, [apiBaseUrl, enabled, tenantId, token])

  useEffect(() => {
    if (!enabled || !webServiceId) {
      return
    }
    const controller = new AbortController()
    const load = async () => {
      setJobsStatus('loading')
      try {
        const page = await fetchSourceBuildJobs(
          apiBaseUrl,
          webServiceId,
          token,
          tenantId,
          controller.signal,
        )
        if (controller.signal.aborted) return
        setJobs(page.items)
        setJobsStatus('success')
      } catch (loadError) {
        if (controller.signal.aborted) return
        setJobsStatus('error')
        setError(messageOf(loadError, 'Unable to load source build history.'))
      }
    }
    void load()
    return () => controller.abort()
  }, [apiBaseUrl, enabled, tenantId, token, webServiceId])

  const visibleJobs = webServiceId
    ? jobs.filter((job) => job.webServiceId === webServiceId)
    : []
  const hasActiveBuild = visibleJobs.some(
    (job) => !terminalStatuses.has(job.state.status),
  )
  useEffect(() => {
    if (!enabled || !webServiceId || !hasActiveBuild) return
    const timer = window.setInterval(() => void refreshJobs(), 2500)
    return () => window.clearInterval(timer)
  }, [enabled, hasActiveBuild, refreshJobs, webServiceId])

  const create = async (sourceRepositoryId: string, targetWebServiceId: string) => {
    setActionStatus('loading')
    setActionError('')
    try {
      const created = await createSourceBuildConfiguration(
        apiBaseUrl,
        sourceRepositoryId,
        targetWebServiceId,
        token,
        tenantId,
      )
      setConfigurations((current) => [...current, created])
      setActionStatus('success')
    } catch (actionErrorValue) {
      setActionStatus('error')
      setActionError(messageOf(actionErrorValue, 'Unable to configure source builds.'))
    }
  }

  const remove = async (configurationId: string) => {
    setActionStatus('loading')
    setActionError('')
    try {
      await deleteSourceBuildConfiguration(apiBaseUrl, configurationId, token, tenantId)
      setConfigurations((current) =>
        current.filter((configuration) => configuration.id !== configurationId),
      )
      setActionStatus('success')
    } catch (actionErrorValue) {
      setActionStatus('error')
      setActionError(messageOf(actionErrorValue, 'Unable to remove source build configuration.'))
    }
  }

  const start = async (configurationId: string) => {
    setActionStatus('loading')
    setActionError('')
    try {
      const created = await startSourceBuild(
        apiBaseUrl,
        configurationId,
        token,
        tenantId,
      )
      setJobs((current) => [created, ...current.filter((job) => job.id !== created.id)])
      setActionStatus('success')
    } catch (actionErrorValue) {
      setActionStatus('error')
      setActionError(messageOf(actionErrorValue, 'Unable to start the source build.'))
    }
  }

  return {
    configurations,
    sourceRepositories,
    jobs: visibleJobs,
    status,
    jobsStatus,
    actionStatus,
    error,
    actionError,
    hasActiveBuild,
    refresh: async () => {
      setStatus('loading')
      setError('')
      try {
        await Promise.all([refreshConfiguration(), refreshJobs()])
        setStatus('success')
      } catch (loadError) {
        setStatus('error')
        setError(messageOf(loadError, 'Unable to refresh source builds.'))
      }
    },
    create,
    remove,
    start,
  }
}

export type SourceBuildsManager = ReturnType<typeof useSourceBuildsManager>
