import { describe, expect, it } from 'vitest'
import { sourceExecuting, sourceInterval, phaseLabel, retryAvailable, settingsEqual } from './model'
import { selectedReference, referenceParams } from './navigation'
import { gitOpsSourceFixture as source, gitOpsRunFixture as run } from './testFixtures'
describe('GitOps execution and command guards', () => {
  it('polls idle source at20s and pending generation or active cleanup at5s', () => {
    expect(sourceInterval(source)).toBe(20000)
    expect(
      sourceInterval({
        ...source,
        synchronization: { ...source.synchronization, requestedGeneration: 2 },
      }),
    ).toBe(5000)
    expect(
      sourceInterval({
        ...source,
        activity: 'Paused',
        latestRun: { ...run, status: 'PendingCleanup' },
      }),
    ).toBe(5000)
    expect(
      sourceExecuting({ ...source, latestRun: { ...run, status: 'AwaitingConfirmation' } }),
    ).toBe(false)
  })
  it('offers Retry only for latest fixed-input failure without pending work', () => {
    expect(retryAvailable(source, run)).toBe(true)
    for (const next of [
      { ...source, activity: 'Paused' as const },
      { ...source, revision: 2 },
      { ...source, synchronization: { ...source.synchronization, currentRunId: 'new' } },
    ])
      expect(retryAvailable(next, run)).toBe(false)
    expect(retryAvailable(source, { ...run, id: 'old' })).toBe(false)
    expect(retryAvailable(source, { ...run, commit: null })).toBe(false)
  })
  it('compares both settings without interpreting unchanged prune as confirmation', () => {
    expect(settingsEqual(source, { pollIntervalSeconds: 300, prune: 'Retain' })).toBe(true)
    expect(settingsEqual(source, { pollIntervalSeconds: 300, prune: 'Prune' })).toBe(false)
  })
})
describe('source-bound URL references', () => {
  it('does not reuse a previous source run or preview', () => {
    const search = referenceParams('?view=gitops&tenant=tenant', 'first', 'run', 'run').toString()
    expect(selectedReference(search, 'first', 'run')).toBe('run')
    expect(
      selectedReference(search.replace('resource=first', 'resource=second'), 'second', 'run'),
    ).toBeNull()
  })
  it('replaces the selection while preserving tenant and view', () => {
    const params = referenceParams(
      '?view=gitops&tenant=tenant&run=old',
      'source',
      'preview',
      'preview',
    )
    expect(params.get('tenant')).toBe('tenant')
    expect(params.has('run')).toBe(false)
    expect(selectedReference(params.toString(), 'source', 'preview')).toBe('preview')
  })
})

it('renders named declaration phases and the nullable lifecycle phase', () => {
  expect(phaseLabel('Prepare')).toBe('Prepare')
  expect(phaseLabel('Bind')).toBe('Bind')
  expect(phaseLabel('Configure')).toBe('Configure')
  expect(phaseLabel(null)).toBe('Lifecycle')
})
