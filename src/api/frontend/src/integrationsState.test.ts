import { describe, expect, it } from 'vitest'
import {
  clearTransientIntegrationParams,
  initialIntegrationSelection,
  integrationSelectionReducer,
  readIntegrationNavigation,
} from './modules/platform/integrationsState'
import type { RepositoryAnalysis, SourceBuildPlan } from './types'

const plan = (candidateKey: string): SourceBuildPlan => ({
  candidateKey,
  kind: 'DotNet',
  displayName: candidateKey,
  sourceRoot: '.',
  manifestPath: `${candidateKey}.csproj`,
  confidence: 'High',
  signals: [],
  warnings: [],
  commands: [],
  startCommand: 'dotnet app.dll',
  dockerfilePreview: 'FROM scratch',
})

const analysis = (...candidates: SourceBuildPlan[]): RepositoryAnalysis => ({
  repositoryId: 'repo-1',
  repositoryFullName: 'lsbV/sample',
  branch: 'main',
  commitSha: 'abc123',
  analyzedAtUtc: '2026-07-10T12:00:00Z',
  detectedFiles: [],
  candidates,
})

describe('integration callback navigation', () => {
  it('opens integrations and captures a successful callback', () => {
    expect(
      readIntegrationNavigation('?view=integrations&github=connected&connectionId=connection-1'),
    ).toEqual({
      view: 'integrations',
      githubNotice: { status: 'connected', connectionId: 'connection-1' },
    })
  })

  it('clears only transient callback values', () => {
    expect(
      clearTransientIntegrationParams(
        'http://localhost:5173/?view=integrations&github=error&reason=denied#source',
      ),
    ).toBe('/?view=integrations#source')
  })

  it('falls back to overview for an unknown view', () => {
    expect(readIntegrationNavigation('?view=unknown')).toEqual({
      view: 'overview',
      githubNotice: null,
    })
  })
})

describe('integration selection reducer', () => {
  it('resets repository state when the account or branch changes', () => {
    const connected = integrationSelectionReducer(initialIntegrationSelection, {
      type: 'connectionsLoaded',
      connectionIds: ['connection-1'],
    })
    const repository = integrationSelectionReducer(connected, {
      type: 'selectRepository',
      repositoryId: 'repo-1',
      defaultBranch: 'main',
    })
    const analyzed = integrationSelectionReducer(repository, {
      type: 'analysisLoaded',
      analysis: analysis(plan('only')),
    })

    expect(analyzed.candidateKey).toBe('only')
    expect(
      integrationSelectionReducer(analyzed, {
        type: 'selectBranch',
        branch: 'release',
      }),
    ).toMatchObject({ branch: 'release', analysis: null, candidateKey: null })
    expect(
      integrationSelectionReducer(analyzed, {
        type: 'selectConnection',
        connectionId: 'connection-2',
      }),
    ).toEqual({ ...initialIntegrationSelection, connectionId: 'connection-2' })
  })

  it('requires explicit candidate selection for a monorepo', () => {
    const state = integrationSelectionReducer(
      {
        ...initialIntegrationSelection,
        connectionId: 'connection-1',
        repositoryId: 'repo-1',
        branch: 'main',
      },
      {
        type: 'analysisLoaded',
        analysis: analysis(plan('api'), plan('worker')),
      },
    )

    expect(state.candidateKey).toBeNull()
  })
})
