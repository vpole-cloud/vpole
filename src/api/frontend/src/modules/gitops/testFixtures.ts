// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { GitOpsSource, GitOpsRun } from './types'
export const gitOpsRunFixture: GitOpsRun = {
  id: 'run',
  sourceId: 'source',
  configurationRevision: 1,
  generation: 1,
  commit: 'a'.repeat(40),
  retryOfRunId: null,
  trigger: 'Manual',
  requestedByUserId: 'author',
  status: 'Blocked',
  createdAtUtc: '2026-11-01T00:00:00Z',
  diagnostics: [],
  operations: null,
  lifecycle: null,
}
export const gitOpsSourceFixture: GitOpsSource = {
  id: 'source',
  revision: 1,
  activity: 'Active',
  prune: 'Retain',
  pollIntervalSeconds: 300,
  repository: {
    provider: 'GitHub',
    connectionId: 'connection',
    externalRepositoryId: 'repo',
    branch: 'main',
    manifestDirectory: '',
    displayUrl: 'https://github.com/org/repo',
  },
  status: 'Blocked',
  synchronization: {
    requestedGeneration: 1,
    completedGeneration: 1,
    currentRunId: null,
    latestRunId: 'run',
    observedCommit: null,
    attemptedCommit: null,
    fullyAppliedCommit: null,
  },
  inventory: { owned: 0, pendingRemoval: 0, revision: 0 },
  latestRun: gitOpsRunFixture,
}
