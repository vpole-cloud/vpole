import type { ResourcePlacement } from '../../types'
import type { GitOpsStatus } from '../../data/gitOps'

export interface TenantDomainZone {
  id: string
  tenantId: string
  technicalName: string | null
  deploymentTargetId: string
  label: string
  domain: string
  kind: 'generated' | 'managed-reservation' | string
  revision: number
  gitOps?: GitOpsStatus | null
  createdAtUtc: string
  updatedAtUtc: string
}

export interface HostnameBinding {
  gitOps?: GitOpsStatus | null
  id: string
  tenantId: string
  technicalName: string | null
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
  gitOps?: GitOpsStatus | null
  binding: HostnameBinding
  resourceDisplayName: string | null
  resourceGroupId: string | null
  placement: ResourcePlacement | null
}
