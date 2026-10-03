export type LoadStatus = 'idle' | 'loading' | 'success' | 'error'
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

export interface ImageInfo {
  server: string
  name: string
  tag: string
}

export interface ProbeConfig {
  path: string
  port: string
}

export interface PortMapping {
  servicePort: number
  targetPort: string
  protocol: string
  name: string
}

export interface WebService {
  id: string
  name: string
  resourceGroupId: string
  isolationScopeId?: string | null
  activeRevision: number
  spec: WebServiceSpec
  exposure: WebServiceExposure
  deployments: WebServiceDeployment[]
}

export interface WebServiceExposure {
  mode: 'Private' | 'Public'
  generatedHostnameLabelPrefix: string
}

export interface WebServiceSpec {
  image: ImageInfo
  config: {
    environmentVariables: Record<string, string>
    portMappings: PortMapping[]
  }
  probes: {
    startup: ProbeConfig
    liveness: ProbeConfig
    readiness: ProbeConfig
  }
  autoscaling?: {
    minReplicas: number
    maxReplicas: number
    metric: {
      type: string
      targetAverageUtilization: number
    }
  } | null
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

export interface WebServiceDeploymentEndpoint {
  kind: string
  host: string
  port: number
  scheme: string
}

export interface WebServiceDeploymentIngress {
  ingressClassName: string
  clusterIssuerName: string
  publicBaseDomain?: string | null
  hostnames: string[]
}

export interface WebServiceDeployment {
  deploymentId: string
  isolationScopeId?: string | null
  runtimeCellId: string
  deploymentTargetId: string
  desiredGeneration: number
  observedGeneration: number
  status: string
  failureCode?: string | null
  compute: ComputeProfileSummary
  endpoints: WebServiceDeploymentEndpoint[]
  resourceRevision: number
  fixedReplicas: number
  ingress?: WebServiceDeploymentIngress | null
  deploymentRevision: number
  desiredOperation: 'Apply' | 'Delete'
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

export interface WebServiceReleaseDeployment {
  resourceDeploymentId: string
  deploymentTargetId: string
  resourceRevision: number
  deploymentGeneration: number
  operation: string
  outcome: string
  completedAtUtc?: string | null
  failureCode?: string | null
  failureMessage?: string | null
}

export interface WebServiceRelease {
  id: string
  resourceRevision: number
  action: string
  outcome: string
  requestedAtUtc: string
  completedAtUtc?: string | null
  failureMessage?: string | null
  deployments: WebServiceReleaseDeployment[]
}

export interface WebServiceReleaseHistory {
  activeRevision: number
  releases: PaginatedResponse<WebServiceRelease>
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

// SourceBuild DTOs serialize these .NET enums numerically.
export type SourceBuildRecipeKind = 0 | 1 | 2
export type SourceBuildPhase = 0 | 1 | 2 | 3 | 4 | 5

export interface SourceBuildRecipe {
  candidateKey: string
  kind: SourceBuildRecipeKind
  sourceRoot: string
  manifestPath: string
  commands: string[]
  startCommand?: string | null
  dockerfile: string
  isCustomized: boolean
}

export interface SourceBuildConfiguration {
  id: string
  sourceRepositoryId: string
  webServiceId: string
  recipe: SourceBuildRecipe
  createdByUserId: string
  createdAtUtc: string
  updatedAtUtc: string
}

export interface SourceBuildJobState {
  status:
    | 'queued'
    | 'cloning'
    | 'analyzing'
    | 'building'
    | 'pushing'
    | 'deploying'
    | 'succeeded'
    | 'failed'
  phase?: SourceBuildPhase | null
  startedAtUtc: string
  completedAtUtc?: string | null
  imageReference?: string | null
  imageDigest?: string | null
  releaseId?: string | null
  failureCode?: string | null
  failureMessage?: string | null
}

export interface SourceBuildJob {
  id: string
  configurationId: string
  sourceRepositoryId: string
  webServiceId: string
  branch: string
  commitSha: string
  recipe: SourceBuildRecipe
  state: SourceBuildJobState
  createdAtUtc: string
  updatedAtUtc: string
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

export interface WebServiceInput {
  exposure: 'Private' | 'Public'
  name: string
  resourceGroupId: string
  isolationScopeId?: string
  imageServer: string
  imageName: string
  imageTag: string
  probePath: string
  probePort: string
  servicePort: number
  targetPort: string
  portName: string
  environmentVariables: Array<{
    key: string
    value: string
  }>
  autoscaling?: WebServiceSpec['autoscaling']
  targets: WebServiceDeploymentInput[]
}

export interface WebServiceDeploymentInput {
  deploymentTargetId?: string
  fixedReplicas: number
  computeProfileId?: string
}

export interface WebServiceDeploymentUpdateInput {
  fixedReplicas: number
  computeProfileId?: string
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
  storageClassNames: string[]
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
  storageClassNames: string[]
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
