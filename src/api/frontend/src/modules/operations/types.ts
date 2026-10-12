import type { ComputeProfileSummary, PaginatedResponse } from '../../types'

export interface DeploymentEndpoint {
  kind: string
  host: string
  port: number
  scheme: string
}

export interface DeploymentIngress {
  ingressClassName: string
  clusterIssuerName: string
  publicBaseDomain?: string | null
  hostnames: string[]
}

export interface ResourcePlacement {
  deploymentId: string
  isolationScopeId?: string | null
  runtimeCellId: string
  deploymentTargetId: string
  desiredGeneration: number
  observedGeneration: number
  status: string
  failureCode?: string | null
  compute: ComputeProfileSummary
  endpoints: DeploymentEndpoint[]
  resourceRevision: number
  fixedReplicas: number
  ingress?: DeploymentIngress | null
  deploymentRevision: number
  desiredOperation: 'Apply' | 'Delete'
}

export interface RuntimeDiagnosticInstance {
  uid: string; name: string; phase: string; ready: boolean; currentGeneration: boolean; restartCount: number; reasons: string[]
  containers: { name: string; kind: 'Application' | 'Init'; previousAvailable: boolean }[]; containersTruncated: boolean
}

export interface ResourceDeploymentDiagnostics {
  deploymentId: string; resourceType: string; resourceId: string; targetId: string; deploymentRevision: number; desiredGeneration: number; checkedAtUtc: string
  diagnostics: { kind: 'Unavailable'; reason: string } | { kind: 'Available'; instances: RuntimeDiagnosticInstance[]; instancesTruncated: boolean
    events: { objectKind: string; objectName: string; reason: string; count: number; lastSeenAtUtc?: string | null }[]; eventsTruncated: boolean; eventsUnavailableReason?: string | null }
}

export interface ResourceEndpointDiscovery {
  resourceType: string
  resourceId: string
  displayName: string
  availability: 'Supported' | 'Unsupported'
  endpoints: { id: string; displayName: string; protocol: string; transport: string; port: number; configuration: { key: string; displayName: string; value: string }[] }[]
  topology: { status: 'NotEvaluated' | 'Eligible' | 'Ineligible'; reasonCode: string | null }
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

export interface RuntimeLogSelection { instanceUid: string; container: string; expectedRevision: number; follow: boolean; previous: boolean; tailLines: number }
export interface RuntimeLogFrame { kind: 'Opened' | 'Output' | 'Ended'; text: string | null; reason: string | null; outputBytes: number }
