import { describe, expect, it } from 'vitest'
import { canEditDesiredConfiguration, type GitOpsStatus } from './gitOps'

const managed: GitOpsStatus = {
  sourceId: 'source',
  sourceUrl: 'https://github.com/lsbV/example',
  blocker: null,
  pendingRemoval: false,
  configurationApplied: true,
}
describe('Git desired configuration permissions', () => {
  it('retains manual resource controls for authorized members', () => {
    expect(canEditDesiredConfiguration(true, null)).toBe(true)
    expect(canEditDesiredConfiguration(true)).toBe(true)
    expect(canEditDesiredConfiguration(false)).toBe(false)
  })
  it('retains ownership while blocked or pending removal', () => {
    expect(canEditDesiredConfiguration(true, managed)).toBe(false)
    expect(canEditDesiredConfiguration(true, { ...managed, blocker: 'SecretValuesMissing' })).toBe(
      false,
    )
    expect(canEditDesiredConfiguration(true, { ...managed, pendingRemoval: true })).toBe(false)
  })
})
