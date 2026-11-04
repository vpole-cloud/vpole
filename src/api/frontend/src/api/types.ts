export type LoadStatus = 'idle' | 'loading' | 'success' | 'error'

export type TenantRole = 'Owner' | 'Admin' | 'Member' | 'Viewer'

export interface Tenant {
  id: string
  name: string
  role: TenantRole
  isDefault: boolean
}

export interface ResourceRegistration {
  registrationId: string
  resourceId: string
  resourceTypeId: string
  displayName: string
  owningPluginId?: string | null
  createdAtUtc: string
  updatedAtUtc: string
}

export interface ComputeProfileSummary {
  profile: {
    computeProfileId: string
    computeProfileRevision: number
  }
  displayName: string
  requests: {
    cpuMillis: number
    memoryMi: number
  }
  limits: {
    cpuMillis: number
    memoryMi: number
  }
  extendedResources: Array<{
    name: string
    quantity: number
  }>
}

export interface AvailableDeploymentTarget {
  id: string
  technicalName: string
  displayName: string
  workloadConfigurationRevision: number
  hasPublicIngress: boolean
  publicBaseDomain?: string | null
}

export interface AvailableComputeProfile {
  id: string
  technicalName: string
  displayName: string
  description: string
  resources: {
    requests: {
      cpuMillis: number
      memoryMi: number
    }
    limits: {
      cpuMillis: number
      memoryMi: number
    }
  }
  architecture: string
  extendedResources: Array<{
    name: string
    quantity: number
  }>
  isDefault: boolean
  revision: number
}

export interface PaginatedResponse<T> {
  items: T[]
  totalCount: number
  pageNumber: number
  pageSize: number
}

export type PoleResourceType = string

export interface AuthViewState {
  initialized: boolean
  authenticated: boolean
  userName?: string
}
