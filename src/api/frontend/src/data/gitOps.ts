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
