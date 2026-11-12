import { buildHeaders, fetchJson, sendJson, sendWithoutResponse } from '../../api/transport'
import { fetchPostgresClusters } from '../postgres/api'
import { fetchSecrets } from '../secrets/api'
import type { SecretMetadata } from '../secrets/types'
import type { PostgresCluster } from '../postgres/types'
import type {
  BackupArchive,
  BackupCatalogEntry,
  BackupDestination,
  BackupDestinationInput,
  BackupOperation,
  BackupPolicy,
  BackupPolicyInput,
  BackupRestoreInput,
} from './types'

export function backupApi(
  baseUrl: string,
  credential: () => string | undefined,
  tenantId?: string,
) {
  const url = (path: string) => `${baseUrl}/api/v1/backups/${path}`
  const read = <T>(path: string, signal: AbortSignal) =>
    fetchJson<T>(url(path), { headers: buildHeaders(credential(), tenantId), signal })
  const write = <T>(path: string, method: string, body: unknown, signal: AbortSignal) =>
    sendJson<T>(url(path), {
      method,
      headers: buildHeaders(credential(), tenantId),
      body: JSON.stringify(body),
      signal,
    })
  const remove = (path: string, revision: number, signal: AbortSignal) =>
    sendWithoutResponse(url(`${path}?expectedRevision=${revision}`), {
      method: 'DELETE',
      headers: buildHeaders(credential(), tenantId),
      signal,
    })
  return {
    destinations: (signal: AbortSignal) => read<BackupDestination[]>('destinations', signal),
    policies: (signal: AbortSignal) => read<BackupPolicy[]>('policies', signal),
    archives: (signal: AbortSignal) => read<BackupArchive[]>('archives', signal),
    operations: (signal: AbortSignal) => read<BackupOperation[]>('operations', signal),
    forgetArchive: (id: string, signal: AbortSignal) =>
      sendWithoutResponse(url(`archives/${encodeURIComponent(id)}?confirmed=true`), {
        method: 'DELETE',
        headers: buildHeaders(credential(), tenantId),
        signal,
      }),
    catalog: (id: string, signal: AbortSignal) =>
      read<BackupCatalogEntry[]>(`archives/${encodeURIComponent(id)}/backups`, signal),
    saveDestination: (
      body: BackupDestinationInput,
      signal: AbortSignal,
      current?: BackupDestination,
    ) =>
      write<BackupDestination>(
        current ? `destinations/${encodeURIComponent(current.id)}` : 'destinations',
        current ? 'PUT' : 'POST',
        {
          name: body.name,
          resourceGroupId: body.resourceGroupId,
          isolationScopeId: body.isolationScopeId,
          endpoint: body.endpoint,
          bucket: body.bucket,
          prefix: body.prefix,
          credentialSecretId: body.credentialSecretId,
          accessKeyIdKey: body.accessKeyIdKey,
          secretAccessKeyKey: body.secretAccessKeyKey,
          ...(current && { expectedRevision: current.revision }),
        },
        signal,
      ),
    savePolicy: (body: BackupPolicyInput, signal: AbortSignal, current?: BackupPolicy) =>
      write<BackupPolicy>(
        current ? `policies/${encodeURIComponent(current.id)}` : 'policies',
        current ? 'PUT' : 'POST',
        {
          name: body.name,
          resourceType: body.resourceType,
          resourceId: body.resourceId,
          destinationId: body.destinationId,
          schedule: body.schedule,
          suspended: body.suspended,
          retentionDays: body.retentionDays,
          ...(current && { expectedRevision: current.revision }),
        },
        signal,
      ),
    deletePolicy: (policy: BackupPolicy, signal: AbortSignal) =>
      remove(`policies/${encodeURIComponent(policy.id)}`, policy.revision, signal),
    deleteDestination: (destination: BackupDestination, signal: AbortSignal) =>
      remove(`destinations/${encodeURIComponent(destination.id)}`, destination.revision, signal),
    run: (policyId: string, idempotencyKey: string, signal: AbortSignal) =>
      write<BackupOperation>(
        `policies/${encodeURIComponent(policyId)}/runs`,
        'POST',
        { idempotencyKey },
        signal,
      ),
    restore: (archiveId: string, body: BackupRestoreInput, signal: AbortSignal) =>
      write<BackupOperation>(
        `archives/${encodeURIComponent(archiveId)}/restores`,
        'POST',
        body,
        signal,
      ),
  }
}

export async function fetchBackupCredentialChoices(
  baseUrl: string,
  credential: () => string | undefined,
  tenantId: string | undefined,
  signal: AbortSignal,
) {
  const items: SecretMetadata[] = []
  let cursor: string | undefined
  do {
    const page = await fetchSecrets(baseUrl, cursor, credential(), tenantId, signal)
    items.push(...page.items)
    cursor = page.nextCursor ?? undefined
  } while (cursor)
  return items
}

export async function fetchBackupClusterChoices(
  baseUrl: string,
  credential: () => string | undefined,
  tenantId: string | undefined,
  signal: AbortSignal,
) {
  const items: PostgresCluster[] = []
  let pageNumber = 1
  let total: number
  do {
    const page = await fetchPostgresClusters(
      baseUrl,
      credential(),
      tenantId,
      signal,
      pageNumber,
      200,
    )
    items.push(...page.items)
    if (page.items.length === 0) break
    total = page.totalCount
    pageNumber++
  } while (items.length < total)
  return items
}
