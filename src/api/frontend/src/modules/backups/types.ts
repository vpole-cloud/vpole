import type { GitOpsStatus } from '../../data/gitOps'

export interface BackupDestinationInput {
  name: string
  resourceGroupId: string
  isolationScopeId: string
  endpoint: string
  bucket: string
  prefix: string
  credentialSecretId: string
  accessKeyIdKey: string
  secretAccessKeyKey: string
}

export interface BackupDestination extends BackupDestinationInput {
  gitOps?: GitOpsStatus | null
  id: string
  revision: number
}

export interface BackupPolicyInput {
  name: string
  resourceType: string
  resourceId: string
  destinationId: string
  schedule: string
  suspended: boolean
  retentionDays: number
}

export interface BackupPolicy extends BackupPolicyInput {
  gitOps?: GitOpsStatus | null
  id: string
  revision: number
  deleting: boolean
  nextScheduledAt?: string | null
  failureCode?: string | null
  lastSuccessfulBackupAt?: string | null
  walArchivingStatus?: string | null
}

export interface BackupArchive {
  id: string
  resourceType: string
  resourceId: string
  resourceName: string
  destinationId: string
  serverName: string
  active: boolean
  createdAt: string
  supportsPointInTimeRecovery: boolean
  storageGiB: number
}

export interface BackupCatalogEntry {
  backupId: string
  completedAt: string
}

export interface BackupOperation {
  id: string
  archiveId: string
  kind: 'Backup' | 'Restore' | 'ScheduledBackup'
  state: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Interrupted'
  createdAt: string
  updatedAt: string
  failureCode?: string | null
  newResourceId?: string | null
  backupId?: string | null
  targetTime?: string | null
}

export interface BackupOperationPage {
  items: BackupOperation[]
  nextCursor: string | null
}

export type BackupRestoreSelection =
  { backupId: string; targetTime?: never } | { targetTime: string; backupId?: never }

export type BackupRestoreInput = BackupRestoreSelection & {
  idempotencyKey: string
  name: string
  confirmed: boolean
  storageGiB?: number
}
