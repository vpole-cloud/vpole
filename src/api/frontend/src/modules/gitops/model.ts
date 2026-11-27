// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type {
  GitOpsSource,
  GitOpsRun,
  GitOpsOperation,
  SourceSettings,
  RepositoryLocation,
} from './types'
export const sections = ['overview', 'resources', 'runs', 'previews', 'settings'] as const
export type GitOpsSection = (typeof sections)[number]
const executing = new Set([
  'Queued',
  'Resolving',
  'Fetching',
  'Applying',
  'WaitingRetry',
  'PendingCleanup',
])
export const runExecuting = (run: Pick<GitOpsRun, 'status'> | null) =>
  run !== null && executing.has(run.status)
export const sourceExecuting = (source: GitOpsSource) =>
  Boolean(source.synchronization.currentRunId) ||
  source.synchronization.requestedGeneration > source.synchronization.completedGeneration ||
  runExecuting(source.latestRun)
export const sourceInterval = (source: GitOpsSource | null) =>
  source && sourceExecuting(source) ? 5000 : 20000
export const sourceLabel = (source: GitOpsSource) =>
  `${source.repository.displayUrl || source.repository.externalRepositoryId} · ${source.repository.branch}${source.repository.manifestDirectory ? '/' + source.repository.manifestDirectory : '/'} `
export const settingsOf = (source: GitOpsSource): SourceSettings => ({
  pollIntervalSeconds: source.pollIntervalSeconds,
  prune: source.prune,
})
export const settingsEqual = (a: SourceSettings, b: SourceSettings) =>
  a.pollIntervalSeconds === b.pollIntervalSeconds && a.prune === b.prune
export const retryAvailable = (source: GitOpsSource, run: GitOpsRun | null) =>
  source.activity === 'Active' &&
  !sourceExecuting(source) &&
  run !== null &&
  run.id === source.synchronization.latestRunId &&
  run.configurationRevision === source.revision &&
  Boolean(run.commit) &&
  (run.status === 'Blocked' || run.status === 'Failed')
export const phaseLabel = (phase: GitOpsOperation['phase']) => phase ?? 'Lifecycle'
export const safeCode = (code: string | null | undefined) =>
  code && /^[a-zA-Z0-9_.-]{1,160}$/.test(code) ? code : 'Unavailable'

export const emptyLocation: RepositoryLocation = {
  provider: 'GitHub',
  connectionId: '',
  externalRepositoryId: '',
  branch: '',
  manifestDirectory: '',
}
