import type { GitOpsStatus } from '../../data/gitOps'

export type GitProviderKind = 'GitHub'
export type GitProviderAccountType = 'User' | 'Organization'
export type GitProviderRepositorySelection = 'All' | 'Selected'
export type SourceBuildPlanKind = 'Dockerfile' | 'DotNet' | 'Node'
export type SourceBuildPlanConfidence = 'High' | 'Medium' | 'Low'

export interface GitProviderConnection {
  id: string
  technicalName: string
  provider: GitProviderKind
  accountLogin: string
  accountType: GitProviderAccountType
  repositorySelection: GitProviderRepositorySelection
  createdAtUtc: string
  updatedAtUtc: string
}

export interface BeginGitHubInstallationResponse {
  installUrl: string
  expiresAtUtc: string
}

export interface ProviderRepository {
  id: string
  name: string
  fullName: string
  htmlUrl: string
  isPrivate: boolean
  defaultBranch: string
  updatedAtUtc?: string | null
}

export interface ProviderBranch {
  name: string
  commitSha: string
  isProtected: boolean
}

export interface GitProviderPage<T> {
  items: T[]
  page: number
  pageSize: number
  hasMore: boolean
}

export interface SourceBuildPlan {
  candidateKey: string
  kind: SourceBuildPlanKind
  displayName: string
  sourceRoot: string
  manifestPath: string
  confidence: SourceBuildPlanConfidence
  signals: string[]
  warnings: string[]
  commands: string[]
  startCommand?: string | null
  dockerfilePreview: string
}

export interface RepositoryAnalysis {
  repositoryId: string
  repositoryFullName: string
  branch: string
  commitSha: string
  analyzedAtUtc: string
  detectedFiles: string[]
  candidates: SourceBuildPlan[]
}

export interface SourceRepository {
  id: string
  gitOps?: GitOpsStatus | null
  technicalName: string
  connectionId: string
  externalRepositoryId: string
  repositoryFullName: string
  repositoryUrl: string
  isPrivate: boolean
  branch: string
  revision: number
  createdAtUtc: string
  updatedAtUtc: string
}
