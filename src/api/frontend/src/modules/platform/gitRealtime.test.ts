import { expect, it } from 'vitest'
import {
  gitInterests,
  matchesGitQuery,
  sourceSelectionCurrent,
  staleGitAnalysis,
} from './gitRealtime'
import type { RepositoryAnalysis, SourceRepository } from '../../types'

it('registers committed metadata only and invalidates both discovery shapes without claiming external coverage', () => {
  expect(gitInterests('tenant', ['git', 'sources'])).toEqual([{ topic: 'git', tenantId: 'tenant' }])
  expect(gitInterests('tenant', ['git', 'repositories', 'connection', 2])).toEqual([])
  const event = { version: 1, topic: 'git', tenantId: 'tenant', resourceId: 'source' } as const
  expect(matchesGitQuery(['git', 'repositories', 'connection', 2], event)).toBe(true)
  expect(matchesGitQuery(['git', 'branches', 'connection', 'repository'], event)).toBe(true)
  expect(matchesGitQuery(['git', 'credentials'], event)).toBe(false)
})
it('flags newer authoritative branch evidence without replacing a saved analysis or inferring removal from partial pagination', () => {
  const analysis = { branch: 'main', commitSha: 'old' } as RepositoryAnalysis
  expect(
    staleGitAnalysis(
      analysis,
      [{ name: 'main', commitSha: 'new', isProtected: false }],
      true,
      false,
    ),
  ).toBe(false)
  expect(
    staleGitAnalysis(
      analysis,
      [{ name: 'main', commitSha: 'new', isProtected: false }],
      true,
      true,
    ),
  ).toBe(true)
  expect(staleGitAnalysis(analysis, [], false, true)).toBe(false)
  expect(staleGitAnalysis(analysis, [], true, true)).toBe(true)
})
it('preserves the selected source snapshot and requires review after removal or changed commit/build plan', () => {
  const source = {
    id: 'source',
    connectionId: 'connection',
    externalRepositoryId: 'repo',
    branch: 'main',
    commitSha: 'old',
    selectedPlan: { candidateKey: 'plan' },
  } as SourceRepository
  expect(sourceSelectionCurrent(source, [source])).toBe(true)
  expect(sourceSelectionCurrent(source, [])).toBe(false)
  expect(sourceSelectionCurrent(source, [{ ...source, commitSha: 'new' }])).toBe(false)
  expect(source.commitSha).toBe('old')
})
