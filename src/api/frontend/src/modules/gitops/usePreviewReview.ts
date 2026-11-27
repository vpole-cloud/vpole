// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { loadReviewedPreview } from './previewModel'
import { useGitOpsQuery } from './useGitOpsQuery'
export function usePreviewReview(sourceId: string, previewId: string, active: boolean) {
  return useGitOpsQuery(['preview-review', sourceId, previewId], active, 20000, (api, signal) =>
    loadReviewedPreview(api, sourceId, previewId, signal),
  )
}
