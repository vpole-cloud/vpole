import {
  ApiError,
  buildHeaders,
  fetchJson,
  sendJson,
  sendWithoutResponse,
} from '../../api/transport'
import type {
  CandidateSearch,
  AdoptionCandidate,
  GitOpsOperation,
  GitOpsPreview,
  GitOpsRun,
  GitOpsSource,
  Page,
  PreviewRequest,
  ResourcePage,
  SourceCommand,
  SourceInput,
  SourceSettings,
} from './types'
export interface GitOpsApi {
  baseUrl: string
  tenantId?: string
  credential: () => string | undefined
}
const root = '/api/v1/gitops/sources'
const segment = encodeURIComponent
const messages: Record<string, string> = {
  'gitops.source-not-found': 'This source or run is unavailable in the current tenant.',
  'gitops.revision-conflict': 'Source configuration changed. Refresh and review your changes.',
  'gitops.idempotency-conflict':
    'This command key belongs to different input. Review the original command.',
  'gitops.source-inactive': 'The current source activity does not allow this command.',
  'gitops.source-deleted': 'This creation key belongs to a deleted source.',
  'gitops.detach-required': 'Detach the owned resources before deleting this source.',
  'gitops.source-busy': 'Source execution is busy. Refresh before trying this command again.',
  'gitops.retry-not-available': 'This run cannot be retried. Use Sync to read the current branch.',
  'gitops.repository-access-denied':
    'Repository access is unavailable. Check the GitHub connection.',
  'gitops.repository-not-found': 'The repository is unavailable through this connection.',
  'gitops.repository-unavailable': 'The repository provider is temporarily unavailable.',
  'gitops.preview-not-found': 'This preview is unavailable in the current source.',
  'gitops.preview-stale': 'The reviewed plan changed. Build and review a new preview.',
  'gitops.preview-input-unavailable':
    'The repository input cannot be read. Detach remains available without Git.',
  'gitops.preview-blocked':
    'The lifecycle plan is blocked by resource identity, ownership or dependencies.',
  'gitops.retain-policy-required':
    'Change the source prune policy to Retain before retaining removed declarations.',
}
export function safeGitOpsError(error: unknown): ApiError {
  if (error instanceof ApiError)
    return new ApiError(
      messages[error.code] ??
        (error.status === 400
          ? 'Review the highlighted request fields.'
          : error.status === 403
            ? 'Your current tenant role does not allow this action.'
            : 'GitOps request failed. Refresh the current server state.'),
      error.status,
      Object.fromEntries(Object.keys(error.fieldErrors).map((key) => [key, ['Invalid value.']])),
      error.code,
    )
  return new ApiError(
    'The server response was not received. Check current state before repeating a command.',
    0,
  )
}
async function safely<T>(action: () => Promise<T>): Promise<T> {
  try {
    return await action()
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw safeGitOpsError(error)
  }
}
export function readGitOps<T>(api: GitOpsApi, path: string, signal: AbortSignal): Promise<T> {
  return safely(() =>
    fetchJson<T>(api.baseUrl + root + path, {
      headers: buildHeaders(api.credential(), api.tenantId),
      signal,
    }),
  )
}
function post<T>(
  api: GitOpsApi,
  request: { path: string; body: unknown; method?: 'POST' | 'PUT' },
  signal: AbortSignal,
): Promise<T> {
  return safely(() =>
    sendJson<T>(api.baseUrl + root + request.path, {
      method: request.method ?? 'POST',
      headers: buildHeaders(api.credential(), api.tenantId),
      body: JSON.stringify(request.body),
      signal,
    }),
  )
}
export const sourcePath = (id: string) => '/' + segment(id)
export const fetchSource = (api: GitOpsApi, id: string, signal: AbortSignal) =>
  readGitOps<GitOpsSource>(api, sourcePath(id), signal)
export const fetchSources = (api: GitOpsApi, page: number, signal: AbortSignal) =>
  readGitOps<Page<GitOpsSource>>(api, `?page=${page}&pageSize=20`, signal)
export const createSource = (api: GitOpsApi, input: SourceInput, signal: AbortSignal) =>
  post<GitOpsSource>(api, { path: '/', body: input }, signal)
export const updateSource = (
  api: GitOpsApi,
  input: SourceSettings & { sourceId: string; expectedRevision: number },
  signal: AbortSignal,
) =>
  post<GitOpsSource>(
    api,
    {
      path: sourcePath(input.sourceId),
      method: 'PUT',
      body: {
        expectedRevision: input.expectedRevision,
        pollIntervalSeconds: input.pollIntervalSeconds,
        prune: input.prune,
      },
    },
    signal,
  )
export async function commandSource(api: GitOpsApi, command: SourceCommand, signal: AbortSignal) {
  const { sourceId, action, ...body } = command
  if (action === 'delete')
    return safely(() =>
      sendWithoutResponse(
        api.baseUrl + root + sourcePath(sourceId) + `?expectedRevision=${body.expectedRevision}`,
        { method: 'DELETE', headers: buildHeaders(api.credential(), api.tenantId), signal },
      ),
    )
  return post<GitOpsSource | { runId?: string; requestedGeneration?: number }>(
    api,
    { path: sourcePath(sourceId) + '/' + action, body },
    signal,
  )
}
export const fetchResources = (
  api: GitOpsApi,
  input: { sourceId: string; page: number },
  signal: AbortSignal,
) =>
  readGitOps<ResourcePage>(
    api,
    `${sourcePath(input.sourceId)}/resources?page=${input.page}&pageSize=50`,
    signal,
  )
export const fetchRuns = (
  api: GitOpsApi,
  input: { sourceId: string; page: number },
  signal: AbortSignal,
) =>
  readGitOps<Page<GitOpsRun>>(
    api,
    `${sourcePath(input.sourceId)}/runs?page=${input.page}&pageSize=20`,
    signal,
  )
export const fetchRun = (
  api: GitOpsApi,
  input: { sourceId: string; runId: string },
  signal: AbortSignal,
) =>
  readGitOps<GitOpsRun>(api, `${sourcePath(input.sourceId)}/runs/${segment(input.runId)}`, signal)
export const fetchOperations = (
  api: GitOpsApi,
  input: { sourceId: string; runId: string; page: number },
  signal: AbortSignal,
) =>
  readGitOps<Page<GitOpsOperation>>(
    api,
    `${sourcePath(input.sourceId)}/runs/${segment(input.runId)}/operations?page=${input.page}&pageSize=50`,
    signal,
  )
export const fetchPreviews = (
  api: GitOpsApi,
  input: { sourceId: string; page: number },
  signal: AbortSignal,
) =>
  readGitOps<Page<GitOpsPreview>>(
    api,
    `${sourcePath(input.sourceId)}/previews?page=${input.page}&pageSize=20`,
    signal,
  )
export const fetchPreview = (
  api: GitOpsApi,
  input: { sourceId: string; previewId: string; page: number },
  signal: AbortSignal,
) =>
  readGitOps<GitOpsPreview>(
    api,
    `${sourcePath(input.sourceId)}/previews/${segment(input.previewId)}?page=${input.page}&pageSize=100`,
    signal,
  )
export const createPreview = (
  api: GitOpsApi,
  input: { sourceId: string; request: PreviewRequest },
  signal: AbortSignal,
) =>
  post<GitOpsPreview>(
    api,
    { path: sourcePath(input.sourceId) + '/previews', body: input.request },
    signal,
  )
export const confirmPreview = (
  api: GitOpsApi,
  input: { sourceId: string; previewId: string; expectedRevision: number },
  signal: AbortSignal,
) =>
  post<{ runId: string }>(
    api,
    {
      path: `${sourcePath(input.sourceId)}/previews/${segment(input.previewId)}/confirm`,
      body: { expectedRevision: input.expectedRevision },
    },
    signal,
  )
export const fetchCandidates = (
  api: GitOpsApi,
  input: { sourceId: string; search: CandidateSearch; page: number },
  signal: AbortSignal,
) => {
  const query = new URLSearchParams({ ...input.search, page: String(input.page), pageSize: '50' })
  return readGitOps<Page<AdoptionCandidate>>(
    api,
    `${sourcePath(input.sourceId)}/adoption-candidates?${query}`,
    signal,
  )
}
