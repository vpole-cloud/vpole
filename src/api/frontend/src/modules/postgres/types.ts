import type { GitOpsStatus } from '../../data/gitOps'
import type { ResourcePlacement } from '../../types'

export type PostgresReplicationMode = 'Asynchronous' | 'SynchronousHa'

export interface PostgresCluster {
  id: string
  name: string
  groupId: string
  revision: number
  lifecycle: 'Prepared' | 'Active' | 'Deleting'
  spec: {
    instances: number
    storage: number
    storageProfileId: string
    version: number
    replicationMode: PostgresReplicationMode
  }
  placement: ResourcePlacement
  gitOps?: GitOpsStatus | null
}

export interface PostgresCapabilities {
  from: number
  to: number
}

export interface PostgresInput {
  name: string
  groupId: string
  version: number
  instances: number
  replicationMode: PostgresReplicationMode
  storage: number
  storageProfileId: string
  computeProfileId?: string
  password: string
  placement: { isolationScopeId?: string; deploymentTargetId: string }
}

export interface PostgresUpdateInput {
  expectedRevision: number
  instances?: number
  replicationMode?: PostgresReplicationMode
  storage?: number
  computeProfileId?: string
}

export interface PostgresManagedDatabaseCandidate {
  id: string
  name: string
  deploymentTargetId: string
  resourceGroupId: string
  isolationScopeId: string
  instances: number
  replicationMode: PostgresReplicationMode
}
