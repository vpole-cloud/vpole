import { useCallback, useEffect, useRef, useState } from 'react'
import {
  createStorageProfile,
  fetchKubernetesPersistentVolumeClaims,
  fetchKubernetesPersistentVolumes,
  fetchKubernetesStorageClasses,
  fetchStorageProfiles,
  fetchPlatformDeploymentTargets,
  updateStorageProfile,
} from '../api'
import type {
  KubernetesPersistentVolume,
  KubernetesPersistentVolumeClaim,
  KubernetesStorageClass,
  LoadStatus,
  StorageProfile,
  StorageProfileInput,
  PlatformDeploymentTarget,
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
  const [targets, setTargets] = useState<PlatformDeploymentTarget[]>([])
  const [deploymentTargetId, selectTarget] = useState('')
  const [storageClasses, setStorageClasses] = useState<KubernetesStorageClass[]>([])
  const [persistentVolumes, setPersistentVolumes] = useState<KubernetesPersistentVolume[]>([])
  const [persistentVolumeClaims, setPersistentVolumeClaims] = useState<KubernetesPersistentVolumeClaim[]>([])
  const [status, setStatus] = useState<LoadStatus>(enabled ? 'loading' : 'idle')
  const [error, setError] = useState('')
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')

  const controllerRef = useRef<AbortController | null>(null)
  const [scope, setScope] = useState({ apiBaseUrl, token, enabled, deploymentTargetId })

  const authenticationChanged = scope.apiBaseUrl !== apiBaseUrl || scope.token !== token || scope.enabled !== enabled
  if (authenticationChanged || scope.deploymentTargetId !== deploymentTargetId) {
    setScope({ apiBaseUrl, token, enabled, deploymentTargetId })
    if (authenticationChanged) {
      setProfiles([])
      setTargets([])
      selectTarget('')
    }
    setStorageClasses([])
    setPersistentVolumes([])
    setPersistentVolumeClaims([])
    setStatus(enabled ? 'loading' : 'idle')
    setError('')
    setSubmitStatus('idle')
    setSubmitError('')
  }

  const requestSequence = useRef(0)
  const load = useCallback((signal?: AbortSignal) => {
    const sequence = ++requestSequence.current
    const current = () => !signal?.aborted && sequence === requestSequence.current
    return Promise.resolve().then(async () => {
      try {
        const [loadedProfiles, loadedTargets] = await Promise.all([
          fetchStorageProfiles(apiBaseUrl, token, signal),
          fetchPlatformDeploymentTargets(apiBaseUrl, token, signal),
        ])
        if (!current()) return
        setProfiles(loadedProfiles)
        setTargets(loadedTargets)
        const target = loadedTargets.find((item) => item.id === deploymentTargetId)
          ?? loadedTargets.find((item) => item.isDefault) ?? loadedTargets[0]
        if ((target?.id ?? '') !== deploymentTargetId) {
          selectTarget(target?.id ?? '')
          if (target) return
        }
        const [loadedClasses, loadedVolumes, loadedClaims] = target ? await Promise.all([
          fetchKubernetesStorageClasses(apiBaseUrl, target.id, token, signal),
          fetchKubernetesPersistentVolumes(apiBaseUrl, target.id, token, signal),
          fetchKubernetesPersistentVolumeClaims(apiBaseUrl, target.id, token, signal),
        ]) : [[], [], []]
        if (!current()) return
        setStorageClasses(loadedClasses)
        setPersistentVolumes(loadedVolumes)
        setPersistentVolumeClaims(loadedClaims)
        setStatus('success')
      } catch (loadError: unknown) {
        if (!current()) {
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
    })
  }, [apiBaseUrl, token, deploymentTargetId])

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

  const refresh = () => enabled ? refreshForScope(controllerRef.current?.signal) : Promise.resolve()

  const create = async (input: StorageProfileInput) => {
    const signal = controllerRef.current?.signal
    if (!enabled || !signal || signal.aborted) return
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
    if (!enabled || !signal || signal.aborted) return
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
    targets,
    deploymentTargetId,
    selectTarget,
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
