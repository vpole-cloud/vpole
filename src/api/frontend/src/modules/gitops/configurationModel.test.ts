import { expect, it } from 'vitest'
import { sourceDraft, sourceDirty, reapplySourceDraft } from './configurationModel'
import { gitOpsSourceFixture as source } from './testFixtures'
it('reapplies only edited settings against an explicitly reviewed revision', () => {
  const original = sourceDraft(source)
  const edited = { ...original, draft: { ...original.draft, pollIntervalSeconds: 600 } }
  const next = { ...source, revision: 2, pollIntervalSeconds: 420, prune: 'Prune' as const }
  expect(sourceDirty(original)).toBe(false)
  expect(sourceDirty(edited)).toBe(true)
  expect(reapplySourceDraft(edited, next)).toEqual({
    baseline: next,
    draft: { pollIntervalSeconds: 600, prune: 'Prune' },
  })
})
