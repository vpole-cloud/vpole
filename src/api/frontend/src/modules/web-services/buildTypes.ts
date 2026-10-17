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

export type SourceBuildRecipeKind = 0 | 1 | 2

export type SourceBuildPhase = 0 | 1 | 2 | 3 | 4 | 5

export interface SourceBuildRecipe {
  candidateKey: string
  kind: SourceBuildRecipeKind
  sourceRoot: string
  manifestPath: string
  commands: string[]
  startCommand?: string | null
  dockerfile: string
  isCustomized: boolean
}

export interface SourceBuildConfiguration {
  id: string
  sourceRepositoryId: string
  webServiceId: string
  recipe: SourceBuildRecipe
  createdByUserId: string
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
