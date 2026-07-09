import { useEffect, useState } from 'react'
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
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')

  const load = async (signal?: AbortSignal) => {
    setStatus('loading')
    setError('')

    try {
      const [loadedProfiles, loadedClasses, loadedVolumes, loadedClaims] =
        await Promise.all([
          fetchStorageProfiles(apiBaseUrl, token, signal),
          fetchKubernetesStorageClasses(apiBaseUrl, token, signal),
          fetchKubernetesPersistentVolumes(apiBaseUrl, token, signal),
          fetchKubernetesPersistentVolumeClaims(apiBaseUrl, token, signal),
        ])

      setProfiles(loadedProfiles)
      setStorageClasses(loadedClasses)
      setPersistentVolumes(loadedVolumes)
      setPersistentVolumeClaims(loadedClaims)
      setStatus('success')
    } catch (loadError) {
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
    }
  }

  useEffect(() => {
    if (!enabled) {
      return
    }

    const controller = new AbortController()
    void load(controller.signal)

    return () => {
      controller.abort()
    }
  }, [apiBaseUrl, enabled, token])

  const refresh = async () => {
    await load()
  }

  const create = async (input: StorageProfileInput) => {
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      await createStorageProfile(apiBaseUrl, input, token)
      await refresh()
      setSubmitStatus('success')
    } catch (submitErrorValue) {
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
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      await updateStorageProfile(apiBaseUrl, id, input, token)
      await refresh()
      setSubmitStatus('success')
    } catch (submitErrorValue) {
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
