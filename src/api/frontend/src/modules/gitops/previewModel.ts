import { ApiError } from '../../api/transport'
import { fetchPreview, type GitOpsApi } from './api'
import { safeCode } from './model'
import type { GitOpsPreview, GitOpsSource, LifecycleAction, PreviewAction } from './types'

export interface ReviewedPreview {
  preview: GitOpsPreview
  actions: PreviewAction[]
}
const actionKeys: Record<
  LifecycleAction,
  'apply' | 'adopt' | 'remove' | 'replace' | 'retain' | 'detach'
> = {
  Apply: 'apply',
  Adopt: 'adopt',
  Remove: 'remove',
  Replace: 'replace',
  Retain: 'retain',
  Detach: 'detach',
}
const incomplete = () =>
  new ApiError(
    'The complete preview could not be verified. Refresh the plan before confirming.',
    409,
    {},
    'gitops.preview-incomplete',
  )
const canonical = (value: unknown): string =>
  JSON.stringify(value, (_key, item: unknown) =>
    item !== null && typeof item === 'object' && !Array.isArray(item)
      ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a.localeCompare(b)))
      : item,
  )
export function previewStamp(preview: GitOpsPreview) {
  return canonical({
    id: preview.id,
    revision: preview.revision,
    sourceRevision: preview.sourceRevision,
    inventoryRevision: preview.inventoryRevision,
    intent: preview.intent,
    state: preview.state,
    commit: preview.commit,
    createdAtUtc: preview.createdAtUtc,
    runId: preview.runId,
    input: preview.input,
    summary: preview.summary,
  })
}
export function validatePreviewPages(pages: readonly GitOpsPreview[]): ReviewedPreview {
  const first = pages[0]
  if (
    !first ||
    first.actions.page !== 1 ||
    !Number.isSafeInteger(first.actions.total) ||
    first.actions.total < 0 ||
    first.summary.total !== first.actions.total ||
    !Number.isSafeInteger(first.actions.pageSize) ||
    first.actions.pageSize < 1 ||
    first.actions.pageSize > 200
  )
    throw incomplete()
  if (new Set(first.summary.warnings).size !== first.summary.warnings.length) throw incomplete()
  const stamp = previewStamp(first)
  const actions: PreviewAction[] = []
  for (const [index, page] of pages.entries()) {
    if (
      previewStamp(page) !== stamp ||
      page.actions.page !== index + 1 ||
      page.actions.pageSize !== first.actions.pageSize ||
      page.actions.total !== first.actions.total ||
      page.actions.items.length !==
        Math.min(
          page.actions.pageSize,
          Math.max(0, page.actions.total - index * page.actions.pageSize),
        ) ||
      (page.actions.items.length === 0 && page.actions.hasMore)
    )
      throw incomplete()
    actions.push(...page.actions.items)
    if (
      actions.length > first.actions.total ||
      page.actions.hasMore !== actions.length < first.actions.total
    )
      throw incomplete()
    if (index < pages.length - 1 && !page.actions.hasMore) throw incomplete()
  }
  if (pages.at(-1)?.actions.hasMore || actions.length !== first.actions.total) throw incomplete()
  const ids = new Set(actions.map((action) => action.id))
  if (
    ids.size !== actions.length ||
    actions.some((action) => action.dependencies.some((id) => !ids.has(id) || id === action.id))
  )
    throw incomplete()
  const counts = { apply: 0, adopt: 0, remove: 0, replace: 0, retain: 0, detach: 0 }
  for (const action of actions) {
    const key = actionKeys[action.action]
    if (!key) throw incomplete()
    counts[key]++
  }
  if (
    Object.entries(counts).some(
      ([key, count]) => first.summary[key as keyof typeof counts] !== count,
    )
  )
    throw incomplete()
  const warnings = [...new Set(actions.flatMap((action) => action.warnings))].sort()
  if (canonical(warnings) !== canonical([...new Set(first.summary.warnings)].sort()))
    throw incomplete()
  return { preview: first, actions }
}
export async function loadReviewedPreview(
  api: GitOpsApi,
  sourceId: string,
  previewId: string,
  signal: AbortSignal,
): Promise<ReviewedPreview> {
  const pages: GitOpsPreview[] = []
  let page = 1
  do {
    signal.throwIfAborted()
    const current = await fetchPreview(api, { sourceId, previewId, page }, signal)
    signal.throwIfAborted()
    if (
      !Number.isSafeInteger(current.actions.total) ||
      current.actions.total < 0 ||
      !Number.isSafeInteger(current.actions.pageSize) ||
      current.actions.pageSize < 1 ||
      current.actions.pageSize > 200 ||
      current.actions.items.length !==
        Math.min(
          current.actions.pageSize,
          Math.max(0, current.actions.total - (page - 1) * current.actions.pageSize),
        ) ||
      current.id !== previewId ||
      current.actions.page !== page ||
      (current.actions.items.length === 0 && current.actions.hasMore)
    )
      throw incomplete()
    if (pages[0] && previewStamp(current) !== previewStamp(pages[0])) throw incomplete()
    pages.push(current)
    if (!current.actions.hasMore) break
    if (
      pages.reduce((count, item) => count + item.actions.items.length, 0) >= current.actions.total
    )
      throw incomplete()
    page++
  } while (pages.at(-1)?.actions.hasMore)
  return validatePreviewPages(pages)
}
export const approvalMatchesSource = (source: GitOpsSource, preview: GitOpsPreview) =>
  source.revision === preview.sourceRevision &&
  source.inventory.revision === preview.inventoryRevision
export const previewActivityAllowed = (source: GitOpsSource, intent: string) =>
  source.activity === 'Active' ||
  source.activity === 'Paused' ||
  (source.activity === 'Detaching' && intent === 'Detach')
export const needsDataLossAcknowledgment = (preview: GitOpsPreview) =>
  preview.summary.warnings.includes('DataMayBeLost') ||
  preview.summary.warnings.includes('ProtectedData')
const warnings: Record<string, string> = {
  ProtectedData: 'The plan affects protected data.',
  DataMayBeLost: 'Stored data may be permanently lost.',
  NoAutomaticDataMigration: 'No automatic data migration, backup or restore is performed.',
  IdentityWillChange: 'Replacement creates a new resource identity.',
  Downtime: 'Replacement may interrupt availability.',
  RedisPersistentVolumeClaimsDeleted:
    'Redis PersistentVolumeClaims are deleted by the owner workflow.',
  PersistentVolumeReclaimPolicyDeterminesDataRetention:
    'Underlying volume retention also depends on the storage class reclaim policy.',
  SecretStoredVersionsDeleted: 'Stored Secret versions are deleted.',
  ReplacementSecretValuesRequired: 'A replacement Secret requires new values supplied outside Git.',
  PostgresStatefulSetDataClaimDeleted: 'The PostgreSQL StatefulSet data claim is deleted.',
  PostgresOperatorClaimsFollowOperatorPolicy:
    'PostgreSQL claim cleanup follows the operator policy.',
  PostgresReplacementRequiresBootstrap:
    'A replacement PostgreSQL cluster requires a new authorized bootstrap.',
}
export const warningDescription = (code: string) =>
  warnings[code] ?? `Additional provider warning: ${safeCode(code)}.`
