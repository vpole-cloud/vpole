// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export const gitOpsSourceResourceType = 'pole/gitops-source'

export interface GitOpsStatus {
  sourceId: string
  sourceUrl: string
  blocker: string | null
  pendingRemoval: boolean
  configurationApplied: boolean
}

export function canEditDesiredConfiguration(canManage: boolean, status?: GitOpsStatus | null) {
  return canManage && !status
}
