import type { ResourcePlacement } from '../../types'

export interface TenantDomainZone {
  id: string
  tenantId: string
  deploymentTargetId: string
  label: string
  domain: string
  kind: 'generated' | 'managed-reservation' | string
  revision: number
  createdAtUtc: string
  updatedAtUtc: string
}

export interface HostnameBinding {
  id: string
  tenantId: string
  tenantDomainZoneId: string
  deploymentTargetId: string
  resourceType: string
  resourceId: string
  resourceDeploymentId: string
  relativeName: string
  hostname: string
  kind: 'generated' | 'custom' | string
  createdAtUtc: string
}

export interface DomainZoneBindingUsage {
  binding: HostnameBinding
  resourceDisplayName: string | null
  resourceGroupId: string | null
  placement: ResourcePlacement | null
}
