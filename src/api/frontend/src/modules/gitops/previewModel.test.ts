// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { afterEach, expect, it, vi } from 'vitest'
import {
  loadReviewedPreview,
  validatePreviewPages,
  approvalMatchesSource,
  needsDataLossAcknowledgment,
} from './previewModel'
import { previewFixture, previewPages } from './previewFixtures'
import { gitOpsSourceFixture } from './testFixtures'
import type { GitOpsPreview } from './types'
afterEach(() => vi.unstubAllGlobals())
it('requires the complete plan and exposes warnings from every page', () => {
  const pages = previewPages()
  const reviewed = validatePreviewPages(pages)
  expect(reviewed.actions).toHaveLength(2)
  expect(needsDataLossAcknowledgment(reviewed.preview)).toBe(true)
  expect(() => validatePreviewPages([pages[0]])).toThrow('complete preview')
})
it.each([
  'id',
  'revision',
  'sourceRevision',
  'inventoryRevision',
  'commit',
  'state',
  'input',
  'summary',
] as const)('rejects changed %s between pages', (field) => {
  const pages = previewPages()
  const second = pages[1]
  const change = {
    id: 'other',
    revision: 2,
    sourceRevision: 2,
    inventoryRevision: 1,
    commit: 'b'.repeat(40),
    state: 'Confirmed',
    input: { kind: 'Inventory' },
    summary: { ...second.summary, warnings: [] },
  }[field]
  pages[1] = { ...second, [field]: change } as GitOpsPreview
  expect(() => validatePreviewPages(pages)).toThrow('complete preview')
})
it.each([
  'duplicate',
  'missing-dependency',
  'counts',
  'warnings',
  'page-order',
  'total',
  'truncated',
] as const)('rejects incomplete or inconsistent %s', (fault) => {
  const pages = previewPages()
  const second = pages[1]
  if (fault === 'duplicate') second.actions.items[0].id = pages[0].actions.items[0].id
  if (fault === 'missing-dependency') second.actions.items[0].dependencies = ['missing']
  if (fault === 'counts') for (const page of pages) page.summary.remove = 0
  if (fault === 'warnings') for (const page of pages) page.summary.warnings = []
  if (fault === 'page-order') second.actions.page = 3
  if (fault === 'total') second.actions.total = 3
  if (fault === 'truncated') second.actions.items = []
  expect(() => validatePreviewPages(pages)).toThrow('complete preview')
})
it('checks both source and inventory revision even when resource count is unchanged', () => {
  const preview = previewFixture()
  expect(approvalMatchesSource(gitOpsSourceFixture, preview)).toBe(true)
  expect(approvalMatchesSource({ ...gitOpsSourceFixture, revision: 2 }, preview)).toBe(false)
  expect(
    approvalMatchesSource(
      { ...gitOpsSourceFixture, inventory: { ...gitOpsSourceFixture.inventory, revision: 1 } },
      preview,
    ),
  ).toBe(false)
})
it('acquires current credentials separately for every page', async () => {
  const pages = previewPages()
  let token = 'first'
  const fetch = vi.fn().mockImplementation((_url, init) => {
    const index = fetch.mock.calls.length - 1
    expect(init.headers.Authorization).toBe(`Bearer ${index === 0 ? 'first' : 'renewed'}`)
    token = 'renewed'
    return Promise.resolve(new Response(JSON.stringify(pages[index])))
  })
  vi.stubGlobal('fetch', fetch)
  const reviewed = await loadReviewedPreview(
    { baseUrl: 'https://api.test', tenantId: 'tenant', credential: () => token },
    'source',
    'preview',
    new AbortController().signal,
  )
  expect(reviewed.actions).toHaveLength(2)
  expect(fetch).toHaveBeenCalledTimes(2)
})
it('does not fetch another page after its scope is canceled', async () => {
  const controller = new AbortController()
  const fetch = vi.fn().mockImplementation(() => {
    controller.abort()
    return Promise.resolve(new Response(JSON.stringify(previewPages()[0])))
  })
  vi.stubGlobal('fetch', fetch)
  await expect(
    loadReviewedPreview(
      { baseUrl: 'https://api.test', credential: () => 'token' },
      'source',
      'preview',
      controller.signal,
    ),
  ).rejects.toMatchObject({ name: 'AbortError' })
  expect(fetch).toHaveBeenCalledTimes(1)
})
