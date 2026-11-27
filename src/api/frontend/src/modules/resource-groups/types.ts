// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { GitOpsStatus } from '../../data/gitOps'
export interface ResourceGroup {
  gitOps?: GitOpsStatus | null
  id: string
  name: string
  description: string
  resourceCount: number
  tags: string[]
}

export interface IsolationScope {
  gitOps?: GitOpsStatus | null
  id: string
  resourceGroupId: string
  name: string
  isDefault: boolean
  lifecycle: 'active' | 'deleting'
  revision: number
  createdAtUtc: string
  updatedAtUtc: string
}

export interface ResourceGroupInput {
  name: string
  description: string
  tags: string[]
}
