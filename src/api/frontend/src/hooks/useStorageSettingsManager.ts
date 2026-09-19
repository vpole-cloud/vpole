import { useCallback, useEffect, useRef, useState } from 'react'
import {
  createStorageProfile,
  fetchKubernetesPersistentVolumeClaims,
  fetchKubernetesPersistentVolumes,
  fetchKubernetesStorageClasses,
  fetchStorageProfiles,
  updateStorageProfile,
} from '../api'
import type {
  KubernetesPersistentVolume,
  KubernetesPersistentVolumeClaim,
  KubernetesStorageClass,
  LoadStatus,
  StorageProfile,
  StorageProfileInput,
} from '../types'

interface UseStorageSettingsManagerOptions {
  apiBaseUrl: string
  token?: string
  enabled: boolean
}

export function useStorageSettingsManager({
  apiBaseUrl,
  token,
  enabled,
}: UseStorageSettingsManagerOptions) {
  const [profiles, setProfiles] = useState<StorageProfile[]>([])
  const [storageClasses, setStorageClasses] = useState<KubernetesStorageClass[]>([])
  const [persistentVolumes, setPersistentVolumes] = useState<KubernetesPersistentVolume[]>([])
  const [persistentVolumeClaims, setPersistentVolumeClaims] = useState<KubernetesPersistentVolumeClaim[]>([])
  const [status, setStatus] = useState<LoadStatus>(enabled ? 'loading' : 'idle')
  const [error, setError] = useState('')
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')

  const controllerRef = useRef<AbortController | null>(null)
  const [scope, setScope] = useState({ apiBaseUrl, token, enabled })

  if (scope.apiBaseUrl !== apiBaseUrl || scope.token !== token || scope.enabled !== enabled) {
    setScope({ apiBaseUrl, token, enabled })
    setProfiles([])
    setStorageClasses([])
    setPersistentVolumes([])
    setPersistentVolumeClaims([])
    setStatus(enabled ? 'loading' : 'idle')
    setError('')
    setSubmitStatus('idle')
    setSubmitError('')
  }

  const load = useCallback((signal?: AbortSignal) => {
    return Promise.all([
      fetchStorageProfiles(apiBaseUrl, token, signal),
      fetchKubernetesStorageClasses(apiBaseUrl, token, signal),
      fetchKubernetesPersistentVolumes(apiBaseUrl, token, signal),
      fetchKubernetesPersistentVolumeClaims(apiBaseUrl, token, signal),
    ]).then(([loadedProfiles, loadedClasses, loadedVolumes, loadedClaims]) => {
      if (signal?.aborted) return
      setProfiles(loadedProfiles)
      setStorageClasses(loadedClasses)
      setPersistentVolumes(loadedVolumes)
      setPersistentVolumeClaims(loadedClaims)
      setStatus('success')
    }, (loadError: unknown) => {
      if (signal?.aborted) {
        return
      }

      setProfiles([])
      setStorageClasses([])
      setPersistentVolumes([])
      setPersistentVolumeClaims([])
      setStatus('error')
      setError(
        loadError instanceof Error
          ? loadError.message
          : 'Unable to load storage settings.',
      )
    })
  }, [apiBaseUrl, token])

  useEffect(() => {
    if (!enabled) {
      return
    }

    const controller = new AbortController()
    controllerRef.current = controller
    void load(controller.signal)

    return () => {
      controller.abort()
    }
  }, [enabled, load])

  const refreshForScope = async (signal?: AbortSignal) => {
    if (signal?.aborted) return
    setStatus('loading')
    setError('')
    await load(signal)
  }

  const refresh = () => refreshForScope(controllerRef.current?.signal)

  const create = async (input: StorageProfileInput) => {
    const signal = controllerRef.current?.signal
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      await createStorageProfile(apiBaseUrl, input, token)
      if (signal?.aborted) return
      await refreshForScope(signal)
      if (signal?.aborted) return
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      if (signal?.aborted) throw submitErrorValue
      setSubmitStatus('error')
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to create the storage profile.',
      )
      throw submitErrorValue
    }
  }

  const update = async (id: string, input: StorageProfileInput) => {
    const signal = controllerRef.current?.signal
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      await updateStorageProfile(apiBaseUrl, id, input, token)
      if (signal?.aborted) return
      await refreshForScope(signal)
      if (signal?.aborted) return
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      if (signal?.aborted) throw submitErrorValue
      setSubmitStatus('error')
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to update the storage profile.',
      )
      throw submitErrorValue
    }
  }

  return {
    profiles,
    storageClasses,
    persistentVolumes,
    persistentVolumeClaims,
    status,
    error,
    submitStatus,
    submitError,
    refresh,
    create,
    update,
  }
}
