export type SourceActivity = 'Active' | 'Paused' | 'Detaching' | 'Detached' | 'Deleted'
export type PrunePolicy = 'Retain' | 'Prune'
export type RunStatus =
  | 'Queued'
  | 'Resolving'
  | 'Fetching'
  | 'Applying'
  | 'WaitingRetry'
  | 'PendingCleanup'
  | 'ConfigurationApplied'
  | 'AlreadyCurrent'
  | 'Blocked'
  | 'Failed'
  | 'Superseded'
  | 'LifecycleCompleted'
  | 'AwaitingConfirmation'
export type LifecycleAction = 'Apply' | 'Adopt' | 'Remove' | 'Replace' | 'Retain' | 'Detach'
export interface Page<T> {
  items: T[]
  page: number
  pageSize: number
  hasMore: boolean
}
export interface RepositoryLocation {
  provider: string
  connectionId: string
  externalRepositoryId: string
  branch: string
  manifestDirectory: string
}
export interface RepositoryDisplay extends RepositoryLocation {
  displayUrl: string
}
export interface SourceSettings {
  pollIntervalSeconds: number
  prune: PrunePolicy
}
export interface SourceInput extends RepositoryLocation, SourceSettings {
  idempotencyKey: string
}
export interface Diagnostic {
  code: string
  file: string | null
  document: number | null
  field: string | null
}
export interface OperationSummary {
  create: number
  update: number
  noOp: number
  blocked: number
  pending: number
  lifecycle: {
    adopt: number
    remove: number
    replace: number
    retain: number
    detach: number
    pendingCleanup: number
    awaitingConfirmation: number
  } | null
}
export interface GitOpsRun {
  id: string
  sourceId: string
  configurationRevision: number
  generation: number
  commit: string | null
  retryOfRunId: string | null
  trigger: string
  requestedByUserId: string | null
  status: RunStatus
  createdAtUtc: string
  diagnostics: Diagnostic[]
  operations: OperationSummary | null
  lifecycle: {
    previewId: string
    authorization: 'AwaitingConfirmation' | 'UserConfirmed' | 'PrunePolicy'
  } | null
}
export interface GitOpsSource extends SourceSettings {
  id: string
  revision: number
  repository: RepositoryDisplay
  status: RunStatus
  activity: SourceActivity
  synchronization: {
    requestedGeneration: number
    completedGeneration: number
    observedCommit: string | null
    attemptedCommit: string | null
    fullyAppliedCommit: string | null
    currentRunId: string | null
    latestRunId: string | null
  }
  inventory: { owned: number; pendingRemoval: number; revision: number }
  latestRun: GitOpsRun | null
}
export interface ResourceIdentity {
  kind: string
  name: string
  resourceId: string
}
export interface SourceResource extends ResourceIdentity {
  resourceTypeId: string | null
  materialization: 'Allocated' | 'Created'
  pendingRemoval: boolean
  blocker: string | null
  lastRunId: string
}
export interface ResourcePage extends Page<SourceResource> {
  inventoryRevision: number
}
export interface GitOpsOperation extends ResourceIdentity {
  phase: 'Prepare' | 'Bind' | 'Configure' | null
  status: 'Pending' | 'Succeeded' | 'Blocked' | 'PendingCleanup' | 'AwaitingConfirmation'
  blocker: string | null
  changed: boolean
  plan: {
    action: 'Create' | 'Update' | 'NoOp' | 'Blocked' | 'Adopt' | 'Replace' | 'Remove' | 'Detach'
    revision: number
    evaluatedAtUtc: string
    changedFields: string[]
  } | null
  lifecycle: {
    actionId: string
    action: LifecycleAction
    lastCompletedStep: string | null
    replacementResourceId: string | null
    replacementName: string | null
    changedFields: string[]
  } | null
}
export interface PreviewAction {
  id: string
  resource: ResourceIdentity
  action: LifecycleAction
  replacement: ResourceIdentity | null
  dependencies: string[]
  changedFields: string[]
  warnings: string[]
}
export interface PreviewSummary {
  apply: number
  adopt: number
  remove: number
  replace: number
  retain: number
  detach: number
  total: number
  warnings: string[]
}
export type PreviewInput =
  | { kind: 'Inventory' }
  | { kind: 'Repository'; repository: RepositoryLocation; desiredHash: string }
export interface GitOpsPreview {
  id: string
  revision: number
  sourceRevision: number
  inventoryRevision: number
  intent: string
  state: 'AwaitingConfirmation' | 'Confirmed' | 'PolicyAuthorized'
  commit: string | null
  createdAtUtc: string
  runId: string | null
  input: PreviewInput
  summary: PreviewSummary
  actions: Page<PreviewAction> & { total: number }
}
export type PreviewRequest = { expectedRevision: number } & (
  | { intent: 'ResourceChanges'; removal: 'Retain' | 'Remove' }
  | { intent: 'Adoption' | 'Removal'; resources: ResourceIdentity[] }
  | {
      intent: 'Replacement'
      replacements: { original: ResourceIdentity; desired: { kind: string; name: string } }[]
    }
  | { intent: 'Relocation'; destination: RepositoryLocation }
  | { intent: 'Detach' }
)
export interface AdoptionCandidate {
  kind: string
  name: string | null
  resourceId: string
  ownerSourceId: string | null
  selectable: boolean
  blocker: string | null
  presence: 'Active' | 'Deleting'
  binding: {
    secret: { name: string; resourceId: string }
    consumer: ResourceIdentity
    usage: 'Runtime' | 'Bootstrap'
    targetScope: { mode: 'All' | 'Selected'; deploymentIds: string[] }
  } | null
}
export type CandidateSearch =
  | { kind: string; name: string }
  | {
      kind: string
      relatedKind: string
      relatedName: string
      consumerKind: string
      consumerName: string
    }
export type SourceCommand = { sourceId: string; expectedRevision: number } & (
  | { action: 'sync'; idempotencyKey: string }
  | { action: 'retry'; idempotencyKey: string; runId: string }
  | { action: 'pause' | 'resume' | 'delete' }
)
