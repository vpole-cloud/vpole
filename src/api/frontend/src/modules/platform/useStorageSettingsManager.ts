import { useLayoutEffect, useMemo, useRef, useState } from 'react'
import { createStorageProfile, fetchKubernetesPersistentVolumeClaims, fetchKubernetesPersistentVolumes, fetchKubernetesStorageClasses, fetchStorageProfiles, fetchPlatformDeploymentTargets, updateStorageProfile } from '../../api'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import type { ScopedQueryData } from '../../data/querySession'
import type { LoadStatus, PlatformDeploymentTarget, StorageProfile, StorageProfileInput } from '../../types'

interface UseStorageSettingsManagerOptions { apiBaseUrl: string; token?: string; enabled: boolean }
export function useStorageSettingsManager({ apiBaseUrl, token, enabled }: UseStorageSettingsManagerOptions) {
  const origin = useApiScope({ baseUrl: apiBaseUrl, token })
  const permission = useRef(enabled)
  useLayoutEffect(() => { permission.current = enabled }, [enabled])
  const current = () => origin.isCurrent() && permission.current === enabled
  const identity = `${origin.identity}:${enabled}`
  const [selection, setSelection] = useState({ identity, id: '' })
  const [accepted, setAccepted] = useState<string | null>(null)
  const inventory = useScopedQuery({ scope: [apiBaseUrl, undefined], key: ['platform-storage', 'inventory'], enabled,
    load: async (signal) => {
      const [profiles, targets] = await Promise.all([fetchStorageProfiles(apiBaseUrl, origin.credential(), signal), fetchPlatformDeploymentTargets(apiBaseUrl, origin.credential(), signal)])
      return { profiles, targets }
    } })
  const targets = inventory.resource?.targets ?? []
  const requested = selection.identity === identity ? selection.id : ''
  const deploymentTargetId = targets.find((item) => item.id === requested)?.id ?? targets.find((item) => item.isDefault)?.id ?? targets[0]?.id ?? ''
  const discovery = useScopedQuery({ scope: [apiBaseUrl, undefined], key: ['platform-storage', 'discovery', deploymentTargetId], enabled: enabled && Boolean(deploymentTargetId),
    load: async (signal) => {
      const [storageClasses, persistentVolumes, persistentVolumeClaims] = await Promise.all([
        fetchKubernetesStorageClasses(apiBaseUrl, deploymentTargetId, origin.credential(), signal),
        fetchKubernetesPersistentVolumes(apiBaseUrl, deploymentTargetId, origin.credential(), signal),
        fetchKubernetesPersistentVolumeClaims(apiBaseUrl, deploymentTargetId, origin.credential(), signal),
      ])
      return { storageClasses, persistentVolumes, persistentVolumeClaims }
    } })
  // Privileged evidence must not remain cached when administrator permission is revoked.
  const client = origin.session.client
  const makeKey = origin.key
  const prefix = useMemo(() => makeKey(['platform-storage']), [makeKey])
  useLayoutEffect(() => {
    if (enabled) return
    void client.cancelQueries({ queryKey: prefix })
    client.removeQueries({ queryKey: prefix })
  }, [client, enabled, prefix])
  useLayoutEffect(() => () => {
    void client.cancelQueries({ queryKey: prefix })
    client.removeQueries({ queryKey: prefix })
  }, [client, prefix])
  const mutation = useScopedMutation(identity)
  const refresh = async () => {
    if (!enabled) return
    await Promise.all([inventory.refresh(), discovery.refresh()])
  }
  const saved = async (profile: StorageProfile) => {
    if (!current()) return
    setAccepted(identity)
    await client.cancelQueries({ queryKey: inventory.queryKey, exact: true })
    if (!current()) return
    // The accepted response is authoritative even if the following read fails.
    client.setQueryData<ScopedQueryData<{ profiles: StorageProfile[]; targets: PlatformDeploymentTarget[] }>>(inventory.queryKey, (cached) => cached && ({
      ...cached, value: { ...cached.value, profiles: cached.value.profiles.some((item) => item.id === profile.id)
        ? cached.value.profiles.map((item) => item.id === profile.id ? profile : item)
        : [...cached.value.profiles, profile] },
    }))
    await inventory.refresh()
    if (!current()) return
    await discovery.refreshAfterWrite()
    if (!current()) return
    // Tenant eligibility can depend on platform profiles; refresh every current-session eligibility query.
    await origin.session.client.invalidateQueries({ predicate: (query) => query.queryKey[1] === origin.session.identity && query.queryKey.includes('eligibility'), refetchType: 'active' })
  }
  const create = async (input: StorageProfileInput) => {
    if (!enabled) return
    setAccepted(null)
    const command = structuredClone(input)
    await mutation.run((signal) => createStorageProfile(apiBaseUrl, command, origin.credential(), signal), saved)
  }
  const update = async (id: string, input: StorageProfileInput) => {
    if (!enabled) return
    setAccepted(null)
    const command = structuredClone(input)
    await mutation.run((signal) => updateStorageProfile(apiBaseUrl, id, command, origin.credential(), signal), saved)
  }
  return { profiles: inventory.resource?.profiles ?? [], targets, deploymentTargetId,
    selectTarget: (id: string) => setSelection({ identity, id }),
    hasDiscovery: Boolean(discovery.resource),
    storageClasses: discovery.resource?.storageClasses ?? [], persistentVolumes: discovery.resource?.persistentVolumes ?? [], persistentVolumeClaims: discovery.resource?.persistentVolumeClaims ?? [],
    status: (!enabled ? 'idle' : inventory.status !== 'success' ? inventory.status : deploymentTargetId ? discovery.status : 'success') as LoadStatus,
    error: inventory.error || discovery.error,
    submitStatus: (!enabled ? 'idle' : mutation.busy ? 'loading' : mutation.error ? 'error' : accepted === identity ? 'success' : 'idle') as LoadStatus,
    submitError: enabled && mutation.error instanceof Error ? mutation.error.message : '', refresh, create, update }
}
