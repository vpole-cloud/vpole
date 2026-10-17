import type { RepositoryAnalysis } from './gitTypes'

export type GitHubConnectionNotice =
  { status: 'connected'; connectionId?: string } | { status: 'error'; reason: string }

export interface IntegrationNavigation {
  view: string
  githubNotice: GitHubConnectionNotice | null
}

export function readIntegrationNavigation(
  search: string,
  registeredViews: readonly string[] = ['overview', 'integrations'],
): IntegrationNavigation {
  const query = new URLSearchParams(search)
  const requestedView = query.get('view')
  const githubStatus = query.get('github')
  const view =
    requestedView && registeredViews.includes(requestedView)
      ? requestedView
      : githubStatus
        ? 'integrations'
        : 'overview'

  if (githubStatus === 'connected') {
    return {
      view,
      githubNotice: {
        status: 'connected',
        connectionId: query.get('connectionId') ?? undefined,
      },
    }
  }

  if (githubStatus === 'error') {
    return {
      view,
      githubNotice: {
        status: 'error',
        reason: query.get('reason') ?? 'unknown-error',
      },
    }
  }

  return { view, githubNotice: null }
}

export function clearTransientIntegrationParams(href: string) {
  const url = new URL(href)
  url.searchParams.delete('github')
  url.searchParams.delete('connectionId')
  url.searchParams.delete('reason')
  return `${url.pathname}${url.search}${url.hash}`
}

export interface IntegrationSelectionState {
  connectionId: string | null
  repositoryId: string | null
  branch: string
  analysis: RepositoryAnalysis | null
  candidateKey: string | null
}

export const initialIntegrationSelection: IntegrationSelectionState = {
  connectionId: null,
  repositoryId: null,
  branch: '',
  analysis: null,
  candidateKey: null,
}

export type IntegrationSelectionAction =
  | { type: 'connectionsLoaded'; connectionIds: string[] }
  | { type: 'selectConnection'; connectionId: string | null }
  | { type: 'selectRepository'; repositoryId: string | null; defaultBranch: string }
  | { type: 'selectBranch'; branch: string }
  | { type: 'analysisLoaded'; analysis: RepositoryAnalysis }
  | { type: 'selectCandidate'; candidateKey: string }

export function integrationSelectionReducer(
  state: IntegrationSelectionState,
  action: IntegrationSelectionAction,
): IntegrationSelectionState {
  switch (action.type) {
    case 'connectionsLoaded': {
      const connectionId =
        state.connectionId && action.connectionIds.includes(state.connectionId)
          ? state.connectionId
          : (action.connectionIds[0] ?? null)
      return connectionId === state.connectionId
        ? state
        : { ...initialIntegrationSelection, connectionId }
    }
    case 'selectConnection':
      return { ...initialIntegrationSelection, connectionId: action.connectionId }
    case 'selectRepository':
      return {
        ...state,
        repositoryId: action.repositoryId,
        branch: action.defaultBranch,
        analysis: null,
        candidateKey: null,
      }
    case 'selectBranch':
      return {
        ...state,
        branch: action.branch,
        analysis: null,
        candidateKey: null,
      }
    case 'analysisLoaded':
      return {
        ...state,
        analysis: action.analysis,
        candidateKey:
          action.analysis.candidates.length === 1
            ? action.analysis.candidates[0].candidateKey
            : null,
      }
    case 'selectCandidate':
      return { ...state, candidateKey: action.candidateKey }
  }
}
