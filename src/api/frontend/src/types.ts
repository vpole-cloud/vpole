export type { DeploymentEndpoint, DeploymentIngress, ResourcePlacement, RuntimeDiagnosticInstance, ResourceDeploymentDiagnostics, ResourceEndpointDiscovery, ResourceDeploymentOperation, ResourceDeploymentOperationsPage, ObservedWorkloadPod, WorkloadObservation, ResourceDeploymentObservation } from './modules/operations/types'
export type { PlatformDeploymentTarget, StorageMediaType, StoragePerformanceTier, StorageProfileTenantAccessMode, StorageProfileTenantAccess, StorageProfileSizeLimits, ResolvedStorageProfile, StorageProfile, StorageProfileInput, KubernetesStorageClass, KubernetesStorageOwner, KubernetesPersistentVolume, KubernetesPersistentVolumeClaim } from './modules/platform/storageTypes'
export type { GitProviderKind, GitProviderAccountType, GitProviderRepositorySelection, SourceBuildPlanKind, SourceBuildPlanConfidence, GitProviderConnection, BeginGitHubInstallationResponse, ProviderRepository, ProviderBranch, GitProviderPage, SourceBuildPlan, RepositoryAnalysis, SourceRepository } from './modules/platform/gitTypes'
export type { SearchHit, OverviewData } from './modules/platform/overviewTypes'
export type { SecretMetadata, SecretInput, SecretBindingInput, SecretUsage, SecretProjection } from './modules/secrets/types'
export type { TenantDomainZone, HostnameBinding, DomainZoneBindingUsage } from './modules/domains/types'
export type { ResourceGroup, IsolationScope, ResourceGroupInput } from './modules/resource-groups/types'
export type { RedisService, RedisInput, RedisUpdateInput } from './modules/redis/types'
export type { PostgresCluster, PostgresCapabilities, PostgresInput, PostgresUpdateInput } from './modules/postgres/types'
export type LoadStatus = 'idle' | 'loading' | 'success' | 'error'
export interface RuntimeLogSelection { instanceUid: string; container: string; expectedRevision: number; follow: boolean; previous: boolean; tailLines: number }
export interface RuntimeLogFrame { kind: 'Opened' | 'Output' | 'Ended'; text: string | null; reason: string | null; outputBytes: number }
export interface AuthViewState {
  initialized: boolean
  authenticated: boolean
  userName?: string
}

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
export interface ResourceConnection {
  id: string; name: string; sourceResourceType: string; sourceResourceId: string; destinationResourceType: string; destinationResourceId: string
  endpointId: string; status: string; generation: number
  targets: { targetId: string; phase: 'Apply' | 'Cleanup'; status: string; host: string; port: number; failureCode: string | null }[]
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
  displayName: string
  workloadConfigurationRevision: number
  hasPublicIngress: boolean
  publicBaseDomain?: string | null
}

export interface AvailableComputeProfile {
  id: string
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

export type { ImageInfo, ProbeConfig, PortMapping, WebService, WebServiceExposure, WebServiceSpec, ConnectionEnvironmentReference, SecretEnvironmentReference, WebServiceDeploymentEndpoint, WebServiceDeploymentIngress, WebServiceDeployment, WebServiceReleaseDeployment, WebServiceRelease, WebServiceRecoveryResponse, WebServiceReleaseHistory, WebServiceInput, WebServiceDeploymentInput, WebServiceDeploymentUpdateInput } from './modules/web-services/types'
export type { SourceBuildLogEntry, SourceBuildLogs, SourceBuildRecipeKind, SourceBuildPhase, SourceBuildRecipe, SourceBuildConfiguration, SourceBuildJobState, SourceBuildJob } from './modules/web-services/buildTypes'
