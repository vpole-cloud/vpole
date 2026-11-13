import type { BackupPolicy, BackupPolicyInput } from './types'

export interface BackupConfigurationVersion {
  id: string
  revision: number
}

export interface BackupPolicyConfiguration {
  baseline: BackupPolicyInput
  version: BackupConfigurationVersion | undefined
  draft: BackupPolicyInput
}

function policyInput(current?: BackupPolicy): BackupPolicyInput {
  return {
    name: current?.name ?? '',
    resourceType: current?.resourceType ?? 'pole/postgresql',
    resourceId: current?.resourceId ?? '',
    destinationId: current?.destinationId ?? '',
    schedule: current?.schedule ?? '0 2 * * *',
    suspended: current?.suspended ?? false,
    retentionDays: current?.retentionDays ?? 30,
  }
}

export function backupPolicyConfiguration(current?: BackupPolicy): BackupPolicyConfiguration {
  const baseline = policyInput(current)
  return {
    baseline,
    version: current && { id: current.id, revision: current.revision },
    draft: { ...baseline },
  }
}

function policyChanges({ baseline, draft }: BackupPolicyConfiguration): Partial<BackupPolicyInput> {
  return Object.fromEntries(
    Object.entries(draft).filter(
      ([key, value]) => value !== baseline[key as keyof BackupPolicyInput],
    ),
  )
}

export function isBackupPolicyDirty(state: BackupPolicyConfiguration): boolean {
  return Object.keys(policyChanges(state)).length > 0
}

export function reapplyBackupPolicyChanges(
  state: BackupPolicyConfiguration,
  current: BackupPolicy,
): BackupPolicyConfiguration {
  const latest = backupPolicyConfiguration(current)
  return { ...latest, draft: { ...latest.draft, ...policyChanges(state) } }
}
