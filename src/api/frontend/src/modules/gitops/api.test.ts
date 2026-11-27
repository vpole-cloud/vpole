// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { afterEach, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/transport'
import {
  commandSource,
  confirmPreview,
  fetchCandidates,
  fetchPreview,
  safeGitOpsError,
} from './api'
const api = { baseUrl: 'https://api.test', tenantId: 'tenant', credential: vi.fn(() => 'current') }
afterEach(() => vi.unstubAllGlobals())
it('sends current credential and tenant on each preview page', async () => {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response('{}')))
  vi.stubGlobal('fetch', fetch)
  await fetchPreview(
    api,
    { sourceId: 'source', previewId: 'preview', page: 2 },
    new AbortController().signal,
  )
  expect(fetch.mock.calls[0][0]).toContain('/sources/source/previews/preview?page=2&pageSize=100')
  expect(fetch.mock.calls[0][1].headers).toMatchObject({
    Authorization: 'Bearer current',
    'X-Pole-Tenant-ID': 'tenant',
  })
})
it('confirmation transmits only stored preview revision', async () => {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response('{"runId":"run"}')))
  vi.stubGlobal('fetch', fetch)
  await confirmPreview(
    api,
    { sourceId: 'source', previewId: 'preview', expectedRevision: 3 },
    new AbortController().signal,
  )
  expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual({ expectedRevision: 3 })
})
it('replays identical Sync input with same command key', async () => {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response('{}')))
  vi.stubGlobal('fetch', fetch)
  const command = {
    sourceId: 'source',
    expectedRevision: 3,
    action: 'sync' as const,
    idempotencyKey: 'same-key',
  }
  await commandSource(api, command, new AbortController().signal)
  await commandSource(api, command, new AbortController().signal)
  expect(fetch.mock.calls[0][1].body).toBe(fetch.mock.calls[1][1].body)
  expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual({
    expectedRevision: 3,
    idempotencyKey: 'same-key',
  })
})
it('encodes related candidates without YAML input', async () => {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response('{}')))
  vi.stubGlobal('fetch', fetch)
  await fetchCandidates(
    api,
    {
      sourceId: 'source',
      search: {
        kind: 'SecretBinding',
        relatedKind: 'Secret',
        relatedName: 'auth',
        consumerKind: 'WebService',
        consumerName: 'app',
      },
      page: 1,
    },
    new AbortController().signal,
  )
  expect(fetch.mock.calls[0][0]).toContain('relatedName=auth')
})
it('keeps arbitrary response text out of displayed errors', () => {
  const error = safeGitOpsError(
    new ApiError('private response body', 409, { Name: ['private field value'] }, 'unknown'),
  )
  expect(error.message).not.toContain('private')
  expect(error.fieldErrors.Name).toEqual(['Invalid value.'])
  expect(
    safeGitOpsError(new ApiError('private', 409, {}, 'gitops.preview-stale')).message,
  ).toContain('new preview')
})
