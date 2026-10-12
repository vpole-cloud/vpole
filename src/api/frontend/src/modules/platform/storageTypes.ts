import type { PoleResourceType } from '../../types'

export interface PlatformDeploymentTarget {
  id: string
  displayName: string
  enabled: boolean
  acceptsWorkloads: boolean
  isDefault: boolean
}

export type StorageMediaType = 'Unknown' | 'HDD' | 'SSD' | 'NVMe' | 'Network'
export type StoragePerformanceTier = 'Standard' | 'Bulk' | 'Fast' | 'Ultra'
export type StorageProfileTenantAccessMode = 'AllTenants' | 'SelectedTenants'
export interface StorageProfileTenantAccess {
  mode: StorageProfileTenantAccessMode
  tenantIds: string[]
}

export interface StorageProfileSizeLimits {
  minSizeGb?: number | null
  maxSizeGb?: number | null
}

export interface ResolvedStorageProfile {
  profileId: string
  displayName: string
  storageClassName: string
  mediaType: StorageMediaType
  performanceTier: StoragePerformanceTier
}

export interface StorageProfile {
  id: string
  displayName: string
  description: string
  mediaType: StorageMediaType
  performanceTier: StoragePerformanceTier
  isEnabled: boolean
  isDefault: boolean
  targetMappings: Array<{ deploymentTargetId: string; storageClassName: string }>
  revision: number
  allowedResourceTypes: PoleResourceType[]
  tenantAccess: StorageProfileTenantAccess
  sizeLimits?: StorageProfileSizeLimits | null
}

export interface StorageProfileInput {
  displayName: string
  description: string
  mediaType: StorageMediaType
  performanceTier: StoragePerformanceTier
  isEnabled: boolean
  isDefault: boolean
  targetMappings: Array<{ deploymentTargetId: string; storageClassName: string }>
  revision?: number
  allowedResourceTypes: PoleResourceType[]
  tenantAccess: StorageProfileTenantAccess
  sizeLimits?: StorageProfileSizeLimits | null
}

export interface KubernetesStorageClass {
  name: string
  provisioner: string
  isDefault: boolean
  reclaimPolicy?: string | null
  volumeBindingMode?: string | null
  allowVolumeExpansion?: boolean | null
  parameters: Record<string, string>
  metadata: Record<string, string>
}

export interface KubernetesStorageOwner {
  tenantId?: string | null
  resourceGroupId?: string | null
  resourceType?: string | null
  resourceId?: string | null
  resourceName?: string | null
}

export interface KubernetesPersistentVolume {
  name: string
  capacity?: string | null
  status?: string | null
  storageClassName?: string | null
  boundClaimNamespace?: string | null
  boundClaimName?: string | null
  reclaimPolicy?: string | null
  owner: KubernetesStorageOwner
}

export interface KubernetesPersistentVolumeClaim {
  name: string
  namespace: string
  capacity?: string | null
  requestedCapacity?: string | null
  status?: string | null
  storageClassName?: string | null
  volumeName?: string | null
  owner: KubernetesStorageOwner
}
