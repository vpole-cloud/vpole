import { expect, it } from 'vitest'
import {
  backupPolicyConfiguration,
  isBackupPolicyDirty,
  reapplyBackupPolicyChanges,
} from './configurationModel'
import type { BackupPolicy } from './types'

const policy: BackupPolicy = {
  id: 'policy',
  archiveId: 'archive',
  revision: 1,
  appliedRevision: 1,
  name: 'nightly',
  resourceType: 'pole/postgresql',
  resourceId: 'cluster',
  destinationId: 'destination',
  schedule: '0 2 * * *',
  suspended: false,
  deleting: false,
  retentionDays: 30,
}

it('retains the original revision and reapplies only user changes to the reviewed revision', () => {
  const original = backupPolicyConfiguration(policy)
  const edited = { ...original, draft: { ...original.draft, schedule: '0 3 * * *' } }
  const latest = { ...policy, revision: 2, retentionDays: 60 }
  expect(edited.version?.revision).toBe(1)
  expect(isBackupPolicyDirty(edited)).toBe(true)
  const reviewed = reapplyBackupPolicyChanges(edited, latest)
  expect(reviewed.version?.revision).toBe(2)
  expect(reviewed.draft).toMatchObject({ schedule: '0 3 * * *', retentionDays: 60 })
  expect(original.draft.schedule).toBe('0 2 * * *')
  expect(isBackupPolicyDirty(backupPolicyConfiguration(latest))).toBe(false)
})
