import type {
  StorageProfile,
  StorageProfileInput,
  StorageMediaType,
  StoragePerformanceTier,
  StorageProfileTenantAccessMode,
} from './storageTypes'
import type { PoleResourceType } from '../../types'
export interface StorageDraft {
  technicalName: string
  displayName: string
  description: string
  mediaType: StorageMediaType
  performanceTier: StoragePerformanceTier
  isEnabled: boolean
  isDefault: boolean
  storageClassName: string
  allowedResourceTypes: PoleResourceType[]
  tenantAccessMode: StorageProfileTenantAccessMode
  tenantIdsText: string
  minSizeGb: string
  maxSizeGb: string
}
export function storageProfileCommand(
  draft: StorageDraft,
  baseline: StorageProfile | null,
  deploymentTargetId: string,
): { input: StorageProfileInput } | { error: string } {
  const tenantIds = draft.tenantIdsText
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)
  const minSize = draft.minSizeGb.trim() ? Number(draft.minSizeGb) : null
  const maxSize = draft.maxSizeGb.trim() ? Number(draft.maxSizeGb) : null
  if (
    !baseline &&
    (!/^[a-z](?:[a-z0-9-]*[a-z0-9])?$/.test(draft.technicalName) || draft.technicalName.length > 63)
  )
    return {
      error:
        'Technical name must be a lowercase DNS label starting with a letter (maximum 63 characters).',
    }
  if (!draft.displayName.trim()) return { error: 'Display name is required.' }
  if (!draft.storageClassName.trim())
    return { error: 'A storage class is required for this target.' }
  if (!draft.allowedResourceTypes.length)
    return { error: 'At least one resource type is required.' }
  if (draft.tenantAccessMode === 'SelectedTenants' && !tenantIds.length)
    return { error: 'At least one tenant id is required for selected tenants.' }
  if (
    [minSize, maxSize].some((size) => size !== null && (!Number.isSafeInteger(size) || size <= 0))
  )
    return { error: 'Size limits must be positive whole numbers.' }
  if (minSize !== null && maxSize !== null && minSize > maxSize)
    return { error: 'Minimum size must be less than or equal to maximum size.' }
  const mapping = { deploymentTargetId, storageClassName: draft.storageClassName.trim() }
  const targetMappings = (baseline?.targetMappings ?? []).map((current) =>
    current.deploymentTargetId === deploymentTargetId ? mapping : { ...current },
  )
  if (!targetMappings.some((current) => current.deploymentTargetId === deploymentTargetId))
    targetMappings.push(mapping)
  return {
    input: {
      technicalName: baseline?.technicalName ?? draft.technicalName,
      displayName: draft.displayName.trim(),
      description: draft.description.trim(),
      mediaType: draft.mediaType,
      performanceTier: draft.performanceTier,
      isEnabled: draft.isEnabled,
      isDefault: draft.isDefault,
      targetMappings,
      ...(baseline ? { revision: baseline.revision } : {}),
      allowedResourceTypes: [...draft.allowedResourceTypes],
      tenantAccess: {
        mode: draft.tenantAccessMode,
        tenantIds: draft.tenantAccessMode === 'SelectedTenants' ? tenantIds : [],
      },
      sizeLimits:
        minSize !== null || maxSize !== null ? { minSizeGb: minSize, maxSizeGb: maxSize } : null,
    },
  }
}
