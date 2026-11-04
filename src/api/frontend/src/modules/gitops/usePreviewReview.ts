import { loadReviewedPreview } from './previewModel'
import { useGitOpsQuery } from './useGitOpsQuery'
export function usePreviewReview(sourceId: string, previewId: string, active: boolean) {
  return useGitOpsQuery(['preview-review', sourceId, previewId], active, 20000, (api, signal) =>
    loadReviewedPreview(api, sourceId, previewId, signal),
  )
}
