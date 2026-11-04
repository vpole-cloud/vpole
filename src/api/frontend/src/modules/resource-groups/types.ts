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
