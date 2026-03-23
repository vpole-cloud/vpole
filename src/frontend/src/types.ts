export type LoadStatus = 'idle' | 'loading' | 'success' | 'error'
export type AppView = 'overview' | 'resource-groups' | 'web-services'

export interface AuthViewState {
  initialized: boolean
  authenticated: boolean
  userName?: string
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
