import type { GitOpsSource, PreviewRequest, RepositoryLocation, ResourceIdentity } from './types'
export type LifecycleIntent = PreviewRequest['intent']
export type LifecycleDraft =
  | { intent: 'ResourceChanges'; removal: 'Retain' | 'Remove' }
  | { intent: 'Adoption'; resources: ResourceIdentity[] }
  | { intent: 'Removal'; resources: ResourceIdentity[] }
  | {
      intent: 'Replacement'
      replacements: { original: ResourceIdentity; desired: { kind: string; name: string } }[]
    }
  | { intent: 'Relocation'; destination: RepositoryLocation }
  | { intent: 'Detach' }
export const resourceIdentity = (value: ResourceIdentity): ResourceIdentity => ({
  kind: value.kind,
  name: value.name,
  resourceId: value.resourceId,
})
export const locationIdentity = (value: RepositoryLocation): RepositoryLocation => ({
  provider: value.provider,
  connectionId: value.connectionId,
  externalRepositoryId: value.externalRepositoryId,
  branch: value.branch,
  manifestDirectory: value.manifestDirectory,
})
export function initialLifecycleDraft(
  intent: LifecycleIntent,
  source: GitOpsSource,
): LifecycleDraft {
  switch (intent) {
    case 'ResourceChanges':
      return { intent, removal: source.prune === 'Retain' ? 'Retain' : 'Remove' }
    case 'Adoption':
    case 'Removal':
      return { intent, resources: [] }
    case 'Replacement':
      return { intent, replacements: [] }
    case 'Relocation':
      return { intent, destination: locationIdentity(source.repository) }
    case 'Detach':
      return { intent }
  }
}
export function previewRequest(draft: LifecycleDraft, revision: number): PreviewRequest {
  switch (draft.intent) {
    case 'ResourceChanges':
      return { intent: draft.intent, expectedRevision: revision, removal: draft.removal }
    case 'Adoption':
    case 'Removal':
      return {
        intent: draft.intent,
        expectedRevision: revision,
        resources: draft.resources.map(resourceIdentity),
      }
    case 'Replacement':
      return {
        intent: draft.intent,
        expectedRevision: revision,
        replacements: draft.replacements.map((item) => ({
          original: resourceIdentity(item.original),
          desired: { kind: item.desired.kind, name: item.desired.name },
        })),
      }
    case 'Relocation':
      return {
        intent: draft.intent,
        expectedRevision: revision,
        destination: locationIdentity(draft.destination),
      }
    case 'Detach':
      return { intent: draft.intent, expectedRevision: revision }
  }
}
const validSelection = (resources: ResourceIdentity[]) =>
  resources.length > 0 &&
  resources.length <= 256 &&
  resources.every((item) => item.name.trim() && item.resourceId && item.kind) &&
  new Set(resources.map((item) => `${item.kind}/${item.resourceId}`)).size === resources.length &&
  new Set(resources.map((item) => `${item.kind}/${item.name}`)).size === resources.length
export function validLifecycleDraft(draft: LifecycleDraft, source: GitOpsSource): boolean {
  switch (draft.intent) {
    case 'ResourceChanges':
      return draft.removal !== 'Retain' || source.prune === 'Retain'
    case 'Adoption':
    case 'Removal':
      return Boolean(validSelection(draft.resources))
    case 'Replacement':
      return Boolean(
        validSelection(draft.replacements.map((item) => item.original)) &&
        draft.replacements.every(
          (item) => item.desired.kind === item.original.kind && item.desired.name.trim(),
        ) &&
        new Set(draft.replacements.map((item) => `${item.desired.kind}/${item.desired.name}`))
          .size === draft.replacements.length,
      )
    case 'Relocation':
      return Boolean(
        draft.destination.provider &&
        draft.destination.connectionId &&
        draft.destination.externalRepositoryId &&
        draft.destination.branch.trim(),
      )
    case 'Detach':
      return true
  }
}
