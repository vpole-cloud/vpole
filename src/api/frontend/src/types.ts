export type { PlatformDeploymentTarget, StorageMediaType, StoragePerformanceTier, StorageProfileTenantAccessMode, StorageProfileTenantAccess, StorageProfileSizeLimits, ResolvedStorageProfile, StorageProfile, StorageProfileInput, KubernetesStorageClass, KubernetesStorageOwner, KubernetesPersistentVolume, KubernetesPersistentVolumeClaim } from './modules/platform/storageTypes'
export type { GitProviderKind, GitProviderAccountType, GitProviderRepositorySelection, SourceBuildPlanKind, SourceBuildPlanConfidence, GitProviderConnection, BeginGitHubInstallationResponse, ProviderRepository, ProviderBranch, GitProviderPage, SourceBuildPlan, RepositoryAnalysis, SourceRepository } from './modules/platform/gitTypes'
export type { SearchHit, OverviewData } from './modules/platform/overviewTypes'
export type { SecretMetadata, SecretInput, SecretBindingInput, SecretUsage, SecretProjection } from './modules/secrets/types'
export type { TenantDomainZone, HostnameBinding, DomainZoneBindingUsage } from './modules/domains/types'
export type { ResourceGroup, IsolationScope, ResourceGroupInput } from './modules/resource-groups/types'
export type { RedisService, RedisInput, RedisUpdateInput } from './modules/redis/types'
export type { PostgresCluster, PostgresCapabilities, PostgresInput, PostgresUpdateInput } from './modules/postgres/types'
import type { WebServiceDeployment } from './modules/web-services/types'
export type LoadStatus = 'idle' | 'loading' | 'success' | 'error'
export interface RuntimeDiagnosticInstance {
  uid: string; name: string; phase: string; ready: boolean; currentGeneration: boolean; restartCount: number; reasons: string[]
  containers: { name: string; kind: 'Application' | 'Init'; previousAvailable: boolean }[]; containersTruncated: boolean
}
export interface ResourceDeploymentDiagnostics {
  deploymentId: string; resourceType: string; resourceId: string; targetId: string; deploymentRevision: number; desiredGeneration: number; checkedAtUtc: string
  diagnostics: { kind: 'Unavailable'; reason: string } | { kind: 'Available'; instances: RuntimeDiagnosticInstance[]; instancesTruncated: boolean
    events: { objectKind: string; objectName: string; reason: string; count: number; lastSeenAtUtc?: string | null }[]; eventsTruncated: boolean; eventsUnavailableReason?: string | null }
}
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

export type ResourcePlacement = WebServiceDeployment

export interface ResourceEndpointDiscovery {
  resourceType: string
  resourceId: string
  displayName: string
  availability: 'Supported' | 'Unsupported'
  endpoints: { id: string; displayName: string; protocol: string; transport: string; port: number; configuration: { key: string; displayName: string; value: string }[] }[]
  topology: { status: 'NotEvaluated' | 'Eligible' | 'Ineligible'; reasonCode: string | null }
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

export interface ResourceDeploymentOperation {
  deployment: { id: string; resourceType: string; resourceId: string; resourceGroupId: string; placement: ResourcePlacement; createdAtUtc: string; updatedAtUtc: string }
  resourceDisplayName: string | null
  phase: 'Waiting' | 'Accepted' | 'Running' | 'Applied' | 'Failed' | 'Deleted'
  attemptCount: number
  sinceUtc: string
  retryable: boolean
  retryNotBeforeUtc: string | null
  operationGeneration?: number
  targetDisplayName?: string | null
}

export interface ResourceDeploymentOperationsPage extends PaginatedResponse<ResourceDeploymentOperation> {
  counts: { waiting: number; accepted: number; running: number; applied: number; failed: number }
  checkedAtUtc: string
}

export interface ObservedWorkloadPod { name: string; phase: string; ready: boolean; currentGeneration: boolean; restartCount: number; reasons: string[] }
export type WorkloadObservation = { kind: 'Unavailable'; reason: string } | {
  kind: 'Observed'; ready: boolean; rolloutCurrent: boolean; desiredReplicas: number; currentReplicas: number;
  updatedReplicas: number; readyReplicas: number; availableReplicas: number; podsTruncated: boolean;
  pods: ObservedWorkloadPod[]; events: { reason: string; count: number; lastSeenAtUtc: string | null }[]; eventsUnavailableReason: string | null
}
export interface ResourceDeploymentObservation {
  deploymentId: string; deploymentRevision: number; desiredGeneration: number; checkedAtUtc: string; observation: WorkloadObservation
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
