// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import {
  createStorageProfile,
  fetchStorageObservation,
  fetchStorageProfiles,
  fetchPlatformDeploymentTargets,
  updateStorageProfile,
} from '../../api'
import { ApiError } from '../../api/transport'
import { usePageVisible } from '../../realtime/usePageVisible'
import { useRealtime } from '../../realtime/context'
import type { StorageContext } from '../../realtime/runtimeContext'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import type { ScopedQueryData } from '../../data/querySession'
import type {
  LoadStatus,
  PlatformDeploymentTarget,
  StorageProfile,
  StorageProfileInput,
  StorageObservation,
} from '../../types'

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
  const origin = useApiScope({ baseUrl: apiBaseUrl, token, privileged: true })
  const controller = useRealtime()
  const permission = useRef(enabled)
  useLayoutEffect(() => {
    permission.current = enabled
  }, [enabled])
  const current = () => origin.isCurrent() && permission.current === enabled
  const identity = `${origin.identity}:${enabled}`
  const [selection, setSelection] = useState({ identity, id: '' })
  const [accepted, setAccepted] = useState<string | null>(null)
  const inventory = useScopedQuery({
    scope: [apiBaseUrl, undefined],
    key: ['platform-storage', 'inventory'],
    enabled,
    privileged: true,
    realtime: [{ topic: 'storage-profiles' }, { topic: 'deployment-targets' }],
    load: async (signal) => {
      const [profiles, targets] = await Promise.all([
        fetchStorageProfiles(apiBaseUrl, origin.credential(), signal),
        fetchPlatformDeploymentTargets(apiBaseUrl, origin.credential(), signal),
      ])
      return { profiles, targets }
    },
  })
  const targets = inventory.resource?.targets ?? []
  const requested = selection.identity === identity ? selection.id : ''
  const deploymentTargetId =
    targets.find((item) => item.id === requested)?.id ??
    targets.find((item) => item.isDefault)?.id ??
    targets[0]?.id ??
    ''
  const connectionVersion =
    targets.find((item) => item.id === deploymentTargetId)?.connection.version ?? 0
  const context: StorageContext = { targetId: deploymentTargetId, connectionVersion }
  const managed = Boolean(controller?.ownsApi(apiBaseUrl))
  const discovery = useScopedQuery<{ snapshot: StorageObservation; epoch?: number }>({
    scope: [apiBaseUrl, undefined],
    key: ['platform-storage', 'discovery', deploymentTargetId, connectionVersion],
    enabled: enabled && Boolean(deploymentTargetId) && connectionVersion > 0,
    privileged: true,
    realtime: [{ topic: 'storage-observations', resourceId: deploymentTargetId }],
    interval: false,
    coveredInterval: 30000,
    load: async (signal) => {
      const started = managed ? controller!.beginObservationRead(apiBaseUrl, context) : undefined
      const snapshot = await fetchStorageObservation(
        apiBaseUrl,
        deploymentTargetId,
        origin.credential(),
        signal,
      )
      // A shared query can outlive its initiating observer. Fence authority and the
      // target context, rather than the initiating hook's mounted lifetime.
      if (
        signal.aborted ||
        !origin.session.isActive() ||
        (controller && (!controller.allowed(apiBaseUrl) || !controller.platformAllowed(apiBaseUrl)))
      )
        throw new DOMException('Aborted', 'AbortError')
      if (
        snapshot.deploymentTargetId !== deploymentTargetId ||
        snapshot.connectionVersion !== connectionVersion
      )
        throw new ApiError('This deployment target changed. Refresh storage inventory.', 409)
      if (
        managed &&
        !controller!.acceptObservationRead(
          apiBaseUrl,
          context,
          started!,
          snapshot.observationVersion,
          signal,
        )
      )
        throw new DOMException('Aborted', 'AbortError')
      if (!snapshot.complete)
        throw new ApiError('Storage inventory is incomplete. Refresh to retry.', 502)
      return { snapshot, epoch: started?.epoch }
    },
  })
  const [retained, setRetained] = useState<{
    identity: string
    snapshot: StorageObservation
  } | null>(null)
  const received = discovery.resource?.snapshot
  if (retained && (!enabled || !origin.isCurrent())) setRetained(null)
  if (
    enabled &&
    received &&
    (retained?.identity !== origin.identity || retained.snapshot !== received)
  )
    setRetained({ identity: origin.identity, snapshot: received })
  const snapshot =
    enabled && origin.isCurrent()
      ? (received ??
        (retained?.identity === origin.identity &&
        retained.snapshot.deploymentTargetId === deploymentTargetId
          ? retained.snapshot
          : null))
      : null
  const visible = usePageVisible()
  const [now, setNow] = useState(Date.now)
  const checkedAtUtc = snapshot?.checkedAtUtc
  useEffect(() => {
    if (!enabled) return
    const resume = () => {
      if (!document.hidden) setNow(Date.now())
    }
    document.addEventListener('visibilitychange', resume)
    return () => document.removeEventListener('visibilitychange', resume)
  }, [enabled])
  useEffect(() => {
    if (!enabled || !visible || !checkedAtUtc) return
    // One local expiry per accepted snapshot, with visibility reactivation. It
    // changes freshness only; uncovered discovery remains manual/visibility based.
    const remaining = Date.parse(checkedAtUtc) + 45000 - Date.now()
    if (remaining < 0) return
    const timer = window.setTimeout(() => setNow(Date.now()), remaining + 1)
    return () => window.clearTimeout(timer)
  }, [enabled, visible, checkedAtUtc])
  const state = managed ? controller!.observationReadState(apiBaseUrl, context) : undefined
  // A response can arrive after its own expiry and after the previous snapshot's
  // timer fired. Query owns the acceptance timestamp; render stays pure.
  const discoveryCurrent = Boolean(
    snapshot &&
    snapshot.complete &&
    Math.max(now, discovery.updatedAt) - Date.parse(snapshot.checkedAtUtc) <= 45000 &&
    snapshot.connectionVersion === connectionVersion &&
    discovery.status !== 'error' &&
    (!state ||
      (discovery.resource?.epoch === state.epoch &&
        (snapshot.observationVersion === null
          ? state.floor === 0
          : snapshot.observationVersion >= state.floor))),
  )
  const writableDiscovery = useRef({ targetId: deploymentTargetId, current: discoveryCurrent })
  useLayoutEffect(() => {
    writableDiscovery.current = { targetId: deploymentTargetId, current: discoveryCurrent }
  }, [deploymentTargetId, discoveryCurrent])
  const canWrite = () =>
    enabled &&
    current() &&
    writableDiscovery.current.targetId === deploymentTargetId &&
    writableDiscovery.current.current
  // Privileged evidence must not remain cached when administrator permission is revoked.
  const client = origin.session.client
  const makeKey = origin.key
  const prefix = useMemo(() => makeKey(['platform-storage']), [makeKey])
  useLayoutEffect(() => {
    if (enabled) return
    void client.cancelQueries({ queryKey: prefix })
    client.removeQueries({ queryKey: prefix })
  }, [client, enabled, prefix])
  useLayoutEffect(
    () => () => {
      // Query observers detach after layout cleanup. A surviving storage view owns
      // the same read; remove privileged evidence only after its last owner leaves.
      queueMicrotask(() => {
        const predicate = (query: { queryKey: readonly unknown[]; isActive: () => boolean }) =>
          prefix.every((part, index) => query.queryKey[index] === part) && !query.isActive()
        void client.cancelQueries({ predicate })
        client.removeQueries({ predicate })
      })
    },
    [client, prefix],
  )
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
    client.setQueryData<
      ScopedQueryData<{ profiles: StorageProfile[]; targets: PlatformDeploymentTarget[] }>
    >(
      inventory.queryKey,
      (cached) =>
        cached && {
          ...cached,
          value: {
            ...cached.value,
            profiles: cached.value.profiles.some((item) => item.id === profile.id)
              ? cached.value.profiles.map((item) => (item.id === profile.id ? profile : item))
              : [...cached.value.profiles, profile],
          },
        },
    )
    await inventory.refresh()
    if (!current()) return
    await discovery.refreshAfterWrite()
    if (!current()) return
    // Tenant eligibility can depend on platform profiles; refresh every current-session eligibility query.
    await origin.session.client.invalidateQueries({
      predicate: (query) =>
        query.queryKey[1] === origin.session.identity && query.queryKey.includes('eligibility'),
      refetchType: 'active',
    })
  }
  const create = async (input: StorageProfileInput) => {
    if (!canWrite()) return
    setAccepted(null)
    const command = structuredClone(input)
    await mutation.run(
      (signal) => createStorageProfile(apiBaseUrl, command, origin.credential(), signal),
      saved,
    )
  }
  const update = async (id: string, input: StorageProfileInput) => {
    if (!canWrite()) return
    setAccepted(null)
    const command = structuredClone(input)
    await mutation.run(
      (signal) => updateStorageProfile(apiBaseUrl, id, command, origin.credential(), signal),
      saved,
    )
  }
  return {
    profiles: inventory.resource?.profiles ?? [],
    targets,
    deploymentTargetId,
    selectTarget: (id: string) => setSelection({ identity, id }),
    hasDiscovery: Boolean(snapshot),
    discoveryCurrent,
    storageClasses: snapshot?.storageClasses ?? [],
    persistentVolumes: snapshot?.persistentVolumes ?? [],
    persistentVolumeClaims: snapshot?.persistentVolumeClaims ?? [],
    status: (!enabled
      ? 'idle'
      : inventory.status !== 'success'
        ? inventory.status
        : deploymentTargetId
          ? discovery.status
          : 'success') as LoadStatus,
    error: inventory.error || discovery.error,
    submitStatus: (!enabled
      ? 'idle'
      : mutation.busy
        ? 'loading'
        : mutation.error
          ? 'error'
          : accepted === identity
            ? 'success'
            : 'idle') as LoadStatus,
    submitError: enabled && mutation.error instanceof Error ? mutation.error.message : '',
    refresh,
    create,
    update,
  }
}
