import type {
  BackupArchive,
  BackupCatalogEntry,
  BackupDestination,
  BackupPolicy,
  BackupRestoreInput,
} from './types'

export type BackupConfirmation =
  | { kind: 'disconnect'; destination: BackupDestination }
  | { kind: 'deletePolicy'; policy: BackupPolicy }
  | { kind: 'forgetArchive'; archive: BackupArchive }
  | {
      kind: 'restoreCopy'
      archive: BackupArchive
      command: Extract<BackupRestoreInput, { backupId: string }>
      copy: BackupCatalogEntry
    }
  | {
      kind: 'restoreTime'
      archive: BackupArchive
      command: Extract<BackupRestoreInput, { targetTime: string }>
    }

export interface BackupConfirmationContext {
  canManage: boolean
  canRestore: boolean
  archiveId: string
  destinations: BackupDestination[] | null
  policies: BackupPolicy[] | null
  archives: BackupArchive[] | null
  catalog: BackupCatalogEntry[] | null
}

function archiveIdentity(archive: BackupArchive) {
  return [
    archive.id,
    archive.resourceType,
    archive.resourceId,
    archive.resourceName,
    archive.destinationId,
    archive.serverName,
    archive.active,
    archive.storageGiB,
    archive.supportsPointInTimeRecovery,
  ]
}

export function canConfirmBackup(
  selection: BackupConfirmation,
  context: BackupConfirmationContext,
): boolean {
  switch (selection.kind) {
    case 'disconnect':
      return (
        context.canManage &&
        Boolean(
          context.destinations?.some(
            (item) =>
              item.id === selection.destination.id &&
              item.revision === selection.destination.revision &&
              !item.gitOps,
          ),
        )
      )
    case 'deletePolicy':
      return (
        context.canManage &&
        Boolean(
          context.policies?.some(
            (item) =>
              item.id === selection.policy.id &&
              item.revision === selection.policy.revision &&
              !item.deleting &&
              !item.gitOps,
          ),
        )
      )
    case 'forgetArchive':
    case 'restoreCopy':
    case 'restoreTime': {
      const current = context.archives?.find((item) => item.id === selection.archive.id)
      if (
        !context.canRestore ||
        !current ||
        current.id !== context.archiveId ||
        JSON.stringify(archiveIdentity(current)) !==
          JSON.stringify(archiveIdentity(selection.archive))
      )
        return false
      if (selection.kind === 'forgetArchive') return !current.active
      if (!context.catalog) return false
      if (selection.kind === 'restoreCopy')
        return context.catalog.some(
          (item) =>
            item.backupId === selection.copy.backupId &&
            item.completedAt === selection.copy.completedAt,
        )
      return (
        current.supportsPointInTimeRecovery &&
        context.catalog.some(
          (item) =>
            new Date(item.completedAt).getTime() <=
            new Date(selection.command.targetTime).getTime(),
        )
      )
    }
  }
}

export function backupConfirmationText(selection: BackupConfirmation): {
  title: string
  description: string
} {
  switch (selection.kind) {
    case 'disconnect':
      return {
        title: `Disconnect ${selection.destination.name}?`,
        description: 'Existing archives must retain an authorized destination.',
      }
    case 'deletePolicy':
      return {
        title: `Delete ${selection.policy.name}?`,
        description: 'Stop this backup policy. Existing copies remain in storage.',
      }
    case 'forgetArchive':
      return {
        title: `Forget archive for ${selection.archive.resourceName}?`,
        description:
          'Remove this archive from Pole. Its backups stay in S3. Restoring this archive through Pole requires its metadata, which this action removes.',
      }
    case 'restoreCopy':
    case 'restoreTime':
      return {
        title: `Restore ${selection.archive.resourceName}?`,
        description: `Create ${selection.command.name} from ${selection.command.backupId ?? selection.command.targetTime} on the source deployment target.`,
      }
  }
}
