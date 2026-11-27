// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { GitOpsStatus } from '../../data/gitOps'
import type { ResourcePlacement } from '../../types'

export interface RedisService {
  id: string
  name: string
  resourceGroupId: string
  spec: { storage: { sizeGb: number; storageProfileId: string } }
  tags: string[]
  revision: number
  lifecycle: 'Prepared' | 'Active' | 'Deleting'
  gitOps?: GitOpsStatus | null
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
