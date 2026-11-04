import type { GitOpsSource, SourceSettings } from './types'
import { settingsEqual, settingsOf } from './model'
export interface SourceDraft {
  baseline: GitOpsSource
  draft: SourceSettings
}
export const sourceDraft = (source: GitOpsSource): SourceDraft => ({
  baseline: source,
  draft: settingsOf(source),
})
export const sourceDirty = (state: SourceDraft) => !settingsEqual(state.baseline, state.draft)
export function reapplySourceDraft(state: SourceDraft, source: GitOpsSource): SourceDraft {
  return {
    baseline: source,
    draft: {
      pollIntervalSeconds:
        state.draft.pollIntervalSeconds !== state.baseline.pollIntervalSeconds
          ? state.draft.pollIntervalSeconds
          : source.pollIntervalSeconds,
      prune: state.draft.prune !== state.baseline.prune ? state.draft.prune : source.prune,
    },
  }
}
