import type { GitOpsPreview, PreviewAction } from './types'
export function previewFixture(count = 2): GitOpsPreview {
  const actions: PreviewAction[] = Array.from({ length: count }, (_, index) => ({
    id: `action-${index}`,
    resource: {
      kind: index === count - 1 ? 'Secret' : 'WebService',
      name: `resource-${index}`,
      resourceId: `resource-${index}`,
    },
    action: 'Remove',
    replacement: null,
    dependencies: index > 0 ? [`action-${index - 1}`] : [],
    changedFields: [],
    warnings:
      index === count - 1 ? ['ProtectedData', 'DataMayBeLost', 'NoAutomaticDataMigration'] : [],
  }))
  return {
    id: 'preview',
    revision: 1,
    sourceRevision: 1,
    inventoryRevision: 0,
    intent: 'ResourceChanges',
    state: 'AwaitingConfirmation',
    commit: 'a'.repeat(40),
    createdAtUtc: '2026-11-01T00:00:00Z',
    runId: null,
    input: {
      kind: 'Repository',
      repository: {
        provider: 'GitHub',
        connectionId: 'connection',
        externalRepositoryId: 'repository',
        branch: 'main',
        manifestDirectory: 'manifests',
      },
      desiredHash: 'hash',
    },
    summary: {
      apply: 0,
      adopt: 0,
      remove: count,
      replace: 0,
      retain: 0,
      detach: 0,
      total: count,
      warnings: count ? ['ProtectedData', 'DataMayBeLost', 'NoAutomaticDataMigration'] : [],
    },
    actions: {
      items: actions.slice(0, 100),
      page: 1,
      pageSize: 100,
      hasMore: count > 100,
      total: count,
    },
  }
}
export function previewPages(): [GitOpsPreview, GitOpsPreview] {
  const first = previewFixture(2)
  first.actions = { ...first.actions, items: [first.actions.items[0]], pageSize: 1, hasMore: true }
  const second = previewFixture(2)
  second.actions = {
    ...second.actions,
    items: [second.actions.items[1]],
    page: 2,
    pageSize: 1,
    hasMore: false,
  }
  return [first, second]
}
export function largePreviewPage(page: number) {
  const full = previewFixture(101)
  if (page === 1) return full
  const last: PreviewAction = {
    id: 'action-100',
    resource: { kind: 'Secret', name: 'resource-100', resourceId: 'resource-100' },
    action: 'Remove',
    replacement: null,
    dependencies: ['action-99'],
    changedFields: [],
    warnings: ['ProtectedData', 'DataMayBeLost', 'NoAutomaticDataMigration'],
  }
  return { ...full, actions: { ...full.actions, items: [last], page: 2, hasMore: false } }
}
