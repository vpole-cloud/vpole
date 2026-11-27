// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export interface SourceBuildLogEntry {
  sequence: number
  stream: 'system' | 'stdout' | 'stderr'
  text: string
  recordedAtUtc: string
}

export interface SourceBuildLogs {
  buildJobId: string
  webServiceId: string
  captureState: 'Open' | 'Completed' | 'Interrupted' | 'Expired' | 'Unavailable'
  entries: SourceBuildLogEntry[]
  nextSequence: number
  lastSequence: number
  hasMore: boolean
  truncated: boolean
  expiresAtUtc: string | null
  capturedBytes: number
  maximumBytes: number | null
}

export type SourceBuildPhase = 0 | 1 | 2 | 3 | 4 | 5

export interface SourceBuildRecipe {
  dockerfile: string
}

export interface SourceBuildConfiguration {
  id: string
  technicalName: string
  revision: number
  gitOps?: import('../../data/gitOps').GitOpsStatus | null
  sourceRepositoryId: string
  webServiceId: string
  recipe: SourceBuildRecipe
  createdByUserId: string | null
  createdAtUtc: string
  updatedAtUtc: string
}

export interface SourceBuildJobState {
  status:
    | 'queued'
    | 'cloning'
    | 'analyzing'
    | 'building'
    | 'pushing'
    | 'deploying'
    | 'succeeded'
    | 'failed'
  phase?: SourceBuildPhase | null
  startedAtUtc: string
  completedAtUtc?: string | null
  imageReference?: string | null
  imageDigest?: string | null
  releaseId?: string | null
  failureCode?: string | null
  failureMessage?: string | null
}

export interface SourceBuildJob {
  id: string
  configurationId: string
  sourceRepositoryId: string
  webServiceId: string
  branch: string
  commitSha: string
  recipe: SourceBuildRecipe
  state: SourceBuildJobState
  createdAtUtc: string
  updatedAtUtc: string
}
