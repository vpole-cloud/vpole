import type { ResourcePlacement } from '../../types'

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
