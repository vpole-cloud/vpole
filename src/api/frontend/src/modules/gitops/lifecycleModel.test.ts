import { expect, it } from 'vitest'
import { initialLifecycleDraft, previewRequest, validLifecycleDraft } from './lifecycleModel'
import { gitOpsSourceFixture as source } from './testFixtures'
import type { SourceResource } from './types'
const resource: SourceResource = {
  kind: 'RedisService',
  name: 'cache',
  resourceId: 'old-id',
  resourceTypeId: 'pole/redis',
  materialization: 'Created',
  pendingRemoval: true,
  blocker: null,
  lastRunId: 'old-run',
}
it('sends only the exact selected identity fields for adoption and removal', () => {
  for (const intent of ['Adoption', 'Removal'] as const)
    expect(previewRequest({ intent, resources: [resource] }, 3)).toEqual({
      intent,
      expectedRevision: 3,
      resources: [{ kind: 'RedisService', name: 'cache', resourceId: 'old-id' }],
    })
})
it('requires explicit same-kind replacement pairs and never infers rename', () => {
  expect(
    previewRequest(
      {
        intent: 'Replacement',
        replacements: [
          { original: resource, desired: { kind: 'RedisService', name: 'new-cache' } },
        ],
      },
      3,
    ),
  ).toEqual({
    intent: 'Replacement',
    expectedRevision: 3,
    replacements: [
      {
        original: { kind: 'RedisService', name: 'cache', resourceId: 'old-id' },
        desired: { kind: 'RedisService', name: 'new-cache' },
      },
    ],
  })
  expect(
    validLifecycleDraft(
      {
        intent: 'Replacement',
        replacements: [{ original: resource, desired: { kind: 'Secret', name: 'new-cache' } }],
      },
      source,
    ),
  ).toBe(false)
})
it('does not send repository display metadata in relocation input', () => {
  const draft = initialLifecycleDraft('Relocation', source)
  expect(previewRequest(draft, 3)).toEqual({
    intent: 'Relocation',
    expectedRevision: 3,
    destination: {
      provider: 'GitHub',
      connectionId: 'connection',
      externalRepositoryId: 'repo',
      branch: 'main',
      manifestDirectory: '',
    },
  })
  expect(JSON.stringify(previewRequest(draft, 3))).not.toContain('displayUrl')
})
it('detach is inventory-only and includes no repository or selected action list', () => {
  expect(previewRequest(initialLifecycleDraft('Detach', source), 3)).toEqual({
    intent: 'Detach',
    expectedRevision: 3,
  })
})
it('requires at least one adoption/removal identity and rejects duplicate addresses or IDs', () => {
  expect(validLifecycleDraft({ intent: 'Adoption', resources: [] }, source)).toBe(false)
  expect(
    validLifecycleDraft(
      { intent: 'Removal', resources: [resource, { ...resource, name: 'another' }] },
      source,
    ),
  ).toBe(false)
  expect(
    validLifecycleDraft(
      { intent: 'Removal', resources: [resource, { ...resource, resourceId: 'another' }] },
      source,
    ),
  ).toBe(false)
  expect(validLifecycleDraft({ intent: 'Removal', resources: [resource] }, source)).toBe(true)
})
it('keeps Retain distinct from Remove and requires the matching source policy', () => {
  expect(validLifecycleDraft({ intent: 'ResourceChanges', removal: 'Retain' }, source)).toBe(true)
  expect(
    validLifecycleDraft(
      { intent: 'ResourceChanges', removal: 'Retain' },
      { ...source, prune: 'Prune' },
    ),
  ).toBe(false)
  expect(previewRequest({ intent: 'ResourceChanges', removal: 'Remove' }, 3)).toEqual({
    intent: 'ResourceChanges',
    expectedRevision: 3,
    removal: 'Remove',
  })
})
it('prevents two replacement selections from reserving the same desired address', () => {
  expect(
    validLifecycleDraft(
      {
        intent: 'Replacement',
        replacements: [
          { original: resource, desired: { kind: 'RedisService', name: 'new' } },
          {
            original: { ...resource, name: 'other', resourceId: 'other' },
            desired: { kind: 'RedisService', name: 'new' },
          },
        ],
      },
      source,
    ),
  ).toBe(false)
})
it('requires complete relocation references', () => {
  const draft = initialLifecycleDraft('Relocation', source)
  expect(validLifecycleDraft(draft, source)).toBe(true)
  if (draft.intent === 'Relocation')
    expect(
      validLifecycleDraft({ ...draft, destination: { ...draft.destination, branch: '' } }, source),
    ).toBe(false)
})
