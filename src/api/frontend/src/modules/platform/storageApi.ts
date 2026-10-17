import { ApiError, buildHeaders, fetchJson, sendJson } from '../../api/transport'
import type {
  StorageProfile,
  StorageProfileInput,
  PlatformDeploymentTarget,
  KubernetesStorageClass,
  KubernetesPersistentVolume,
  KubernetesPersistentVolumeClaim,
  StorageObservation,
} from './storageTypes'

export async function fetchStorageProfiles(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<StorageProfile[]>(`${apiBaseUrl}/api/v1/platform/storage/profiles`, {
    headers: buildHeaders(token),
    signal,
  })
}

export async function fetchPlatformDeploymentTargets(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<PlatformDeploymentTarget[]>(`${apiBaseUrl}/api/v1/platform/deployment-targets`, {
    headers: buildHeaders(token),
    signal,
  })
}

export async function fetchKubernetesStorageClasses(
  apiBaseUrl: string,
  deploymentTargetId: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<KubernetesStorageClass[]>(
    `${apiBaseUrl}/api/v1/platform/storage/storage-classes?${new URLSearchParams({ deploymentTargetId })}`,
    {
      headers: buildHeaders(token),
      signal,
    },
  )
}

export async function fetchKubernetesPersistentVolumes(
  apiBaseUrl: string,
  deploymentTargetId: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<KubernetesPersistentVolume[]>(
    `${apiBaseUrl}/api/v1/platform/storage/persistent-volumes?${new URLSearchParams({ deploymentTargetId })}`,
    {
      headers: buildHeaders(token),
      signal,
    },
  )
}

export async function fetchKubernetesPersistentVolumeClaims(
  apiBaseUrl: string,
  deploymentTargetId: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<KubernetesPersistentVolumeClaim[]>(
    `${apiBaseUrl}/api/v1/platform/storage/persistent-volume-claims?${new URLSearchParams({ deploymentTargetId })}`,
    {
      headers: buildHeaders(token),
      signal,
    },
  )
}

export async function createStorageProfile(
  apiBaseUrl: string,
  input: StorageProfileInput,
  token?: string,
  signal?: AbortSignal,
) {
  return sendJson<StorageProfile>(`${apiBaseUrl}/api/v1/platform/storage/profiles`, {
    method: 'POST',
    headers: buildHeaders(token),
    signal,
    body: JSON.stringify(input),
  })
}

export async function updateStorageProfile(
  apiBaseUrl: string,
  id: string,
  input: StorageProfileInput,
  token?: string,
  signal?: AbortSignal,
) {
  return sendJson<StorageProfile>(
    `${apiBaseUrl}/api/v1/platform/storage/profiles/${encodeURIComponent(id)}`,
    {
      method: 'PUT',
      headers: buildHeaders(token),
      signal,
      body: JSON.stringify(input),
    },
  )
}

export async function fetchStorageObservation(
  apiBaseUrl: string,
  deploymentTargetId: string,
  token?: string,
  signal?: AbortSignal,
) {
  const value = await fetchJson<StorageObservation>(
    `${apiBaseUrl}/api/v1/platform/storage/observation?${new URLSearchParams({ deploymentTargetId })}`,
    { headers: buildHeaders(token), signal },
  )
  if (
    !value ||
    typeof value.deploymentTargetId !== 'string' ||
    !Number.isSafeInteger(value.connectionVersion) ||
    value.connectionVersion <= 0 ||
    (value.observationVersion !== null &&
      (!Number.isSafeInteger(value.observationVersion) || value.observationVersion < 0)) ||
    !Number.isFinite(Date.parse(value.checkedAtUtc)) ||
    typeof value.complete !== 'boolean' ||
    !Array.isArray(value.storageClasses) ||
    !Array.isArray(value.persistentVolumes) ||
    !Array.isArray(value.persistentVolumeClaims)
  )
    throw new ApiError('Storage inventory response is unavailable. Refresh to retry.', 502)
  return value
}
