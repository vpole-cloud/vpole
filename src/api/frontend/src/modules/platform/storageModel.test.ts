import { expect, it } from 'vitest'
import { storageProfileCommand, type StorageDraft } from './storageModel'
import type { StorageProfile } from './storageTypes'
const draft: StorageDraft = { displayName: ' Data disk ', description: '', mediaType: 'SSD', performanceTier: 'Standard', isEnabled: true, isDefault: false, storageClassName: 'updated', allowedResourceTypes: ['pole/redis'], tenantAccessMode: 'AllTenants', tenantIdsText: 'old-tenant', minSizeGb: '', maxSizeGb: '' }
it('preserves the baseline revision and other target mappings when shaping an edited profile', () => {
  const baseline = { revision: 7, targetMappings: [{ deploymentTargetId: 'a', storageClassName: 'original' }, { deploymentTargetId: 'b', storageClassName: 'retained' }] } as StorageProfile
  const command = storageProfileCommand(draft, baseline, 'a')
  expect(command).toMatchObject({ input: { revision: 7, displayName: 'Data disk', targetMappings: [{ deploymentTargetId: 'a', storageClassName: 'updated' }, { deploymentTargetId: 'b', storageClassName: 'retained' }], tenantAccess: { mode: 'AllTenants', tenantIds: [] } } })
  expect(baseline.targetMappings[0].storageClassName).toBe('original')
})
it.each(['NaN', '0', '-1', '1.5'])('rejects invalid capacity %s before JSON can silently change it', (minSizeGb) => {
  expect(storageProfileCommand({ ...draft, minSizeGb }, null, 'a')).toEqual({ error: 'Size limits must be positive whole numbers.' })
})
