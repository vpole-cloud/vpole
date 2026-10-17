import { ApiError, buildHeaders, request } from '../../api/transport'
import type {
  SecretMetadata,
  SecretInput,
  SecretBindingInput,
  SecretUsage,
  SecretProjection,
} from './types'

const secretProblems: Record<string, string> = {
  'secrets.managed-by-provider': 'Change this managed credential through its owning resource.',
  'secrets.not-found': 'Secret not found in this tenant.',
  'secrets.name-already-exists': 'This Secret name already exists in the tenant.',
  'secrets.reference-rejected':
    'The selected resource or deployment is unavailable in this group and isolation scope. Refresh the choices.',
  'secrets.encryption-unavailable': 'Secret encryption is temporarily unavailable.',
  'secrets.version-conflict':
    'The Secret version changed. Refresh metadata and review the complete replacement before rotating again.',
  'secrets.mapped-keys-required':
    'Keep every source key used by an existing binding in the replacement.',
  'secrets.binding-limit-reached': 'This Secret has reached its binding limit.',
  'secrets.consumer-already-bound': 'This resource already has a binding to the Secret.',
  'secrets.unknown-keys': 'A mapping uses an unknown Secret key.',
  'secrets.bindings-remain': 'Detach all bindings before deleting this Secret.',
}

export class SecretApiError extends ApiError {
  readonly code: string
  constructor(status: number, code: string) {
    super(
      secretProblems[code] ??
        (status === 403
          ? 'You do not have permission to change this Secret.'
          : status === 400
            ? 'Check the metadata and complete key/value map.'
            : 'The Secret request failed. Refresh metadata before retrying.'),
      status,
    )
    this.name = 'SecretApiError'
    this.code = code
  }
}

// Never expose arbitrary response text, validation messages or request values on a Secret error.
async function secretRequest<T>(
  base: string,
  path: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  method = 'GET',
  body?: unknown,
): Promise<T> {
  let response: Response
  try {
    response = await request(`${base}/api/v1/secrets${path}`, {
      method,
      headers: {
        ...buildHeaders(token, tenantId),
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      },
      cache: 'no-store',
      signal,
      ...(body ? { body: JSON.stringify(body) } : {}),
    })
  } catch {
    if (signal?.aborted) throw new DOMException('Request cancelled', 'AbortError')
    throw new SecretApiError(0, '')
  }
  if (!response.ok) {
    let code = ''
    try {
      const problem: unknown = await response.json()
      if (
        problem &&
        typeof problem === 'object' &&
        'code' in problem &&
        typeof problem.code === 'string' &&
        Object.hasOwn(secretProblems, problem.code)
      )
        code = problem.code
    } catch {
      /* Body contents must never escape this transport. */
    }
    throw new SecretApiError(response.status, code)
  }
  if (response.status === 204) return undefined as T
  try {
    return (await response.json()) as T
  } catch {
    throw new SecretApiError(response.status, '')
  }
}

export function fetchSecrets(
  base: string,
  after: string | undefined,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({ pageSize: '50' })
  if (after) query.set('after', after)
  return secretRequest<{ items: SecretMetadata[]; nextCursor: string | null }>(
    base,
    `/?${query}`,
    token,
    tenantId,
    signal,
  )
}
export const fetchSecret = (
  base: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) => secretRequest<SecretMetadata>(base, `/${encodeURIComponent(id)}`, token, tenantId, signal)
export const createSecret = (
  base: string,
  input: SecretInput,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) => secretRequest<SecretMetadata>(base, '/', token, tenantId, signal, 'POST', input)
export const rotateSecret = (
  base: string,
  id: string,
  expectedVersion: number,
  values: Record<string, string>,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) =>
  secretRequest<SecretMetadata>(
    base,
    `/${encodeURIComponent(id)}/versions`,
    token,
    tenantId,
    signal,
    'POST',
    { expectedVersion, values },
  )
export const deleteSecret = (
  base: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) => secretRequest<void>(base, `/${encodeURIComponent(id)}`, token, tenantId, signal, 'DELETE')
export const fetchSecretUsage = (
  base: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) => secretRequest<SecretUsage[]>(base, `/${encodeURIComponent(id)}/usage`, token, tenantId, signal)
export const fetchSecretProjections = (
  base: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) =>
  secretRequest<SecretProjection[]>(
    base,
    `/${encodeURIComponent(id)}/projections`,
    token,
    tenantId,
    signal,
  )
export const bindSecret = (
  base: string,
  id: string,
  input: SecretBindingInput,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) =>
  secretRequest<SecretMetadata>(
    base,
    `/${encodeURIComponent(id)}/bindings`,
    token,
    tenantId,
    signal,
    'POST',
    input,
  )
export const detachSecret = (
  base: string,
  id: string,
  bindingId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) =>
  secretRequest<SecretMetadata>(
    base,
    `/${encodeURIComponent(id)}/bindings/${encodeURIComponent(bindingId)}`,
    token,
    tenantId,
    signal,
    'DELETE',
  )
