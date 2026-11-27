// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { afterEach, expect, it, vi } from 'vitest'
import { backupApi } from './api'

afterEach(() => vi.unstubAllGlobals())

it('reads the archive catalog again and uses the current tenant credential on each request', async () => {
  let token = 'first'
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(
      new Response('[{"backupId":"older","completedAt":"2026-11-12T02:00:00Z"}]'),
    )
    .mockResolvedValueOnce(new Response('[]'))
  vi.stubGlobal('fetch', fetch)
  const client = backupApi('https://api.test', () => token, 'tenant')
  expect(await client.catalog('archive', new AbortController().signal)).toHaveLength(1)
  token = 'renewed'
  expect(await client.catalog('archive', new AbortController().signal)).toEqual([])
  expect(fetch.mock.calls[1][1].headers).toMatchObject({
    Authorization: 'Bearer renewed',
    'X-Pole-Tenant-ID': 'tenant',
  })
})

it('propagates inaccessible catalogs instead of presenting an empty list', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValue(new Response('{"code":"backups.catalog-unavailable"}', { status: 503 })),
  )
  await expect(
    backupApi('https://api.test', () => 'token', 'tenant').catalog(
      'archive',
      new AbortController().signal,
    ),
  ).rejects.toMatchObject({ status: 503 })
})

it('retries a confirmed restore with the same selected ID and operation key', async () => {
  const fetch = vi
    .fn()
    .mockImplementation(() => Promise.resolve(new Response('{"id":"operation"}')))
  vi.stubGlobal('fetch', fetch)
  const client = backupApi('https://api.test', () => 'token', 'tenant')
  const body = { idempotencyKey: 'stable', name: 'restored', backupId: 'older', confirmed: true }
  await client.restore('archive', body, new AbortController().signal)
  await client.restore('archive', body, new AbortController().signal)
  expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual(body)
  expect(fetch.mock.calls[1][1].body).toBe(fetch.mock.calls[0][1].body)
})
