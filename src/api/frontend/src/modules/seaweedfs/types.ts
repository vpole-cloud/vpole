import type { ResourcePlacement } from '../../types'

export const seaweedFSType = 'pole/seaweedfs'
export type SeaweedFSMode = 'SingleNode' | 'Replicated'
export interface SeaweedFSSpec {
  existingPostgresId?: string | null
  mode: SeaweedFSMode
  storage: { sizeGb: number; storageProfileId: string }
}
export interface SeaweedFSInput {
  name: string
  resourceGroupId: string
  spec: SeaweedFSSpec
  tags: string[]
  computeProfileId?: string
  placement: { isolationScopeId: string; deploymentTargetId: string }
}
export interface SeaweedFSInstance {
  id: string
  name: string
  resourceGroupId: string
  spec: SeaweedFSSpec
  tags: string[]
  placement: ResourcePlacement | null
  revision: number
  lifecycle: string
  deletionDeadlineUtc: string | null
}
export interface SeaweedFSBucket {
  id: string
  name: string
  revision: number
  lifecycle: string
  deletionDeadlineUtc: string | null
  secretId: string
}
