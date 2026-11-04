import type { GitOpsStatus } from '../../data/gitOps'
import type { PaginatedResponse, ResourcePlacement } from '../../types'

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
  gitOps?: GitOpsStatus | null
  tags?: string[]
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
    connectionEnvironmentReferences?: Record<string, ConnectionEnvironmentReference>
    secretEnvironmentReferences?: Record<string, SecretEnvironmentReference>
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

export interface ConnectionEnvironmentReference {
  connectionId: string
  valueKind: 'Host' | 'Port' | 'Configuration'
  configurationKey?: string | null
}

export interface SecretEnvironmentReference {
  secretId: string
  key: string
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
  traceId?: string | null
  correlationId?: string | null
  actorUserId: string | null
  actorEmail: string | null
  spec: WebServiceSpec
  provenance: {
    source: string
    commitSha: string | null
    buildUrl: string | null
    imageDigest: string | null
    previousImageTag: string
    newImageTag: string
    actorType: string
    deploymentTokenId: string | null
    deploymentTokenName: string | null
    buildJobId: string | null
    sourceRepositoryId: string | null
    gitOpsSourceId: string | null
    gitOpsRunId: string | null
  } | null
}

export interface WebServiceRecoveryResponse {
  id: string
  resourceRevision: number
  releaseId: string
  deployments: ResourcePlacement[]
}

export interface WebServiceReleaseHistory {
  activeRevision: number
  releases: PaginatedResponse<WebServiceRelease>
}

export interface WebServiceInput {
  expectedRevision?: number
  connectionEnvironmentReferences?: Record<string, ConnectionEnvironmentReference>
  secretEnvironmentReferences?: Record<string, SecretEnvironmentReference>
  tags?: string[]
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
export type WebServiceDeployment = ResourcePlacement
export type {
  DeploymentEndpoint as WebServiceDeploymentEndpoint,
  DeploymentIngress as WebServiceDeploymentIngress,
} from '../operations/types'
