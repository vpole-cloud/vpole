import type { WebService, WebServiceDeployment } from './modules/web-services/types'
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

export interface ResourceGroup {
  id: string
  name: string
  description: string
  resourceCount: number
  tags: string[]
}

export interface IsolationScope {
  id: string
  resourceGroupId: string
  name: string
  isDefault: boolean
  lifecycle: 'active' | 'deleting'
  revision: number
  createdAtUtc: string
  updatedAtUtc: string
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

export interface PlatformDeploymentTarget {
  id: string
  displayName: string
  enabled: boolean
  acceptsWorkloads: boolean
  isDefault: boolean
}

export type ResourcePlacement = WebServiceDeployment

export interface SecretMetadata {
  id: string
  tenantId: string
  resourceGroupId: string
  isolationScopeId: string
  name: string
  purpose: string
  keyNames: string[]
  currentVersion: number
  revision: number
  lifecycle: string
  createdAtUtc: string
  updatedAtUtc: string
  managedBy?: { resourceType: string; resourceId: string; bindingId: string } | null
}

export interface SecretInput {
  resourceGroupId: string
  isolationScopeId: string
  name: string
  purpose: string
  values: Record<string, string>
}

export interface SecretBindingInput {
  consumerType: string
  consumerId: string
  allDeployments: boolean
  deploymentIds: string[]
  keyMappings: Record<string, string>
}

export interface SecretUsage {
  binding: SecretBindingInput & { id: string }
  affectedDeploymentIds: string[]
}

export interface SecretProjection {
  resourceDeploymentId: string
  bindingId: string
  deploymentTargetId: string
  desiredVersion: number
  appliedVersion: number | null
  health: 'Pending' | 'Ready' | 'Failed' | 'Deleted'
  lastAttemptAtUtc: string | null
  failureCode: string | null
}

export interface ResourceEndpointDiscovery {
  resourceType: string
  resourceId: string
  displayName: string
  availability: 'Supported' | 'Unsupported'
  endpoints: { id: string; displayName: string; protocol: string; transport: string; port: number; configuration: { key: string; displayName: string; value: string }[] }[]
  topology: { status: 'NotEvaluated' | 'Eligible' | 'Ineligible'; reasonCode: string | null }
}

export interface RedisService {
  id: string
  name: string
  resourceGroupId: string
  spec: { storage: { sizeGb: number; storageProfileId: string } }
  tags: string[]
  revision: number
  lifecycle: 'Active' | 'Deleting'
  placement: ResourcePlacement | null
}

export interface RedisInput {
  name: string
  resourceGroupId: string
  spec: RedisService['spec']
  tags: string[]
  computeProfileId?: string
  placement: { isolationScopeId?: string; deploymentTargetId: string }
}

export type RedisUpdateInput = Pick<RedisInput, 'spec' | 'tags' | 'computeProfileId'>

export interface PostgresCluster {
  id: string
  name: string
  groupId: string
  revision: number
  lifecycle: 'Active' | 'Deleting'
  spec: {
    instances: number
    storage: number
    storageProfileId: string
    version: number
    deploymentMode: 'Operator' | 'StatefulSet'
  }
  placement: ResourcePlacement
}

export interface PostgresCapabilities {
  from: number
  to: number
  defaultDeploymentMode: 'Operator' | 'StatefulSet'
}

export interface PostgresInput {
  name: string
  groupId: string
  version: number
  instances: number
  storage: number
  storageProfileId: string
  computeProfileId?: string
  username: string
  password: string
  placement: { isolationScopeId?: string; deploymentTargetId: string }
}

export interface PostgresUpdateInput {
  instances: number
  storage: number
  computeProfileId?: string
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

export interface SearchHit {
  resourceTypeId: string
  sourceType: string
  sourceId: string
  title: string
  resourceGroupId?: string | null
  resourceGroupName?: string | null
  operationalStatus?: { status: string; failureCode?: string | null } | null
}

export type GitProviderKind = 'GitHub'
export type GitProviderAccountType = 'User' | 'Organization'
export type GitProviderRepositorySelection = 'All' | 'Selected'
export type SourceBuildPlanKind = 'Dockerfile' | 'DotNet' | 'Node'
export type SourceBuildPlanConfidence = 'High' | 'Medium' | 'Low'

export interface GitProviderConnection {
  id: string
  provider: GitProviderKind
  accountLogin: string
  accountType: GitProviderAccountType
  repositorySelection: GitProviderRepositorySelection
  createdAtUtc: string
  updatedAtUtc: string
}

export interface BeginGitHubInstallationResponse {
  installUrl: string
  expiresAtUtc: string
}

export interface ProviderRepository {
  id: string
  name: string
  fullName: string
  htmlUrl: string
  isPrivate: boolean
  defaultBranch: string
  updatedAtUtc?: string | null
}

export interface ProviderBranch {
  name: string
  commitSha: string
  isProtected: boolean
}

export interface GitProviderPage<T> {
  items: T[]
  page: number
  pageSize: number
  hasMore: boolean
}

export interface SourceBuildPlan {
  candidateKey: string
  kind: SourceBuildPlanKind
  displayName: string
  sourceRoot: string
  manifestPath: string
  confidence: SourceBuildPlanConfidence
  signals: string[]
  warnings: string[]
  commands: string[]
  startCommand?: string | null
  dockerfilePreview: string
}

export interface RepositoryAnalysis {
  repositoryId: string
  repositoryFullName: string
  branch: string
  commitSha: string
  analyzedAtUtc: string
  detectedFiles: string[]
  candidates: SourceBuildPlan[]
}

export interface SourceRepository {
  id: string
  connectionId: string
  externalRepositoryId: string
  repositoryFullName: string
  repositoryUrl: string
  isPrivate: boolean
  branch: string
  commitSha: string
  analyzedAtUtc: string
  createdAtUtc: string
  updatedAtUtc: string
  selectedPlan: SourceBuildPlan
}

export interface OverviewData {
  resourceGroups: ResourceGroup[]
  webServices: WebService[]
}

export interface ResourceGroupInput {
  name: string
  description: string
  tags: string[]
}

export type StorageMediaType = 'Unknown' | 'HDD' | 'SSD' | 'NVMe' | 'Network'
export type StoragePerformanceTier = 'Standard' | 'Bulk' | 'Fast' | 'Ultra'
export type StorageProfileTenantAccessMode = 'AllTenants' | 'SelectedTenants'
export type PoleResourceType = string

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

export type { ImageInfo, ProbeConfig, PortMapping, WebService, WebServiceExposure, WebServiceSpec, ConnectionEnvironmentReference, SecretEnvironmentReference, WebServiceDeploymentEndpoint, WebServiceDeploymentIngress, WebServiceDeployment, WebServiceReleaseDeployment, WebServiceRelease, WebServiceRecoveryResponse, WebServiceReleaseHistory, WebServiceInput, WebServiceDeploymentInput, WebServiceDeploymentUpdateInput } from './modules/web-services/types'
export type { SourceBuildLogEntry, SourceBuildLogs, SourceBuildRecipeKind, SourceBuildPhase, SourceBuildRecipe, SourceBuildConfiguration, SourceBuildJobState, SourceBuildJob } from './modules/web-services/buildTypes'
