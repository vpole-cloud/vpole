import type { ResourcePlacement } from '../../types'

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
  expectedRevision: number
  instances?: number
  storage?: number
  computeProfileId?: string
}
