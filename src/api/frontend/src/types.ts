export type LoadStatus = 'idle' | 'loading' | 'success' | 'error'
export type AppView =
  | 'overview'
  | 'resource-groups'
  | 'web-services'
  | 'integrations'
  | 'storage'

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

export interface ShortResourceInfo {
  id: string
  name: string
  type: string
}

export interface ResourceGroup {
  id: string
  name: string
  description: string
  resources: ShortResourceInfo[]
  tags: string[]
}

export interface ImageInfo {
  server: string
  name: string
  tag: string
}

export interface WebServiceResources {
  requests: {
    cpuCores: number
    memoryMi: number
  }
  limits: {
    cpuCores: number
    memoryMi: number
  }
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
  dnsRecordShortInfo?: {
    id: string
    domain: string
  }
  spec: {
    replicas: number
    image: ImageInfo
    resources: WebServiceResources
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
    } | null
  }
}

export interface PaginatedResponse<T> {
  items: T[]
  totalCount: number
  pageNumber: number
  pageSize: number
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

export interface WebServiceInput {
  name: string
  resourceGroupId: string
  domainName: string
  imageServer: string
  imageName: string
  imageTag: string
  replicas: number
  requestCpuCores: number
  requestMemoryMi: number
  limitCpuCores: number
  limitMemoryMi: number
  probePath: string
  probePort: string
  servicePort: number
  targetPort: string
  portName: string
  environmentVariables: Array<{
    key: string
    value: string
  }>
}

export type StorageMediaType = 'Unknown' | 'HDD' | 'SSD' | 'NVMe' | 'Network'
export type StoragePerformanceTier = 'Standard' | 'Bulk' | 'Fast' | 'Ultra'
export type StorageProfileTenantAccessMode = 'AllTenants' | 'SelectedTenants'
export type PoleResourceType = 'WebService' | 'Redis' | 'Postgres'

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
