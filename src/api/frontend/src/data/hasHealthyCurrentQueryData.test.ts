// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { expect, it } from 'vitest'
import { hasHealthyCurrentQueryData } from './hasHealthyCurrentQueryData'

const loaded = { resource: [], isCurrent: true, status: 'success' as const, error: '' }

it('keeps loaded empty and nonempty metadata eligible while its same-scope background read runs', () => {
  expect(hasHealthyCurrentQueryData(loaded)).toBe(true)
  expect(hasHealthyCurrentQueryData({ ...loaded, status: 'loading' })).toBe(true)
  expect(
    hasHealthyCurrentQueryData({ ...loaded, resource: [{ id: 'current' }], status: 'loading' }),
  ).toBe(true)
})

it.each([
  { label: 'initial loading', query: { ...loaded, resource: null, status: 'loading' as const } },
  {
    label: 'disabled or unauthorized data',
    query: { ...loaded, resource: null, status: 'idle' as const },
  },
  { label: 'another query identity', query: { ...loaded, isCurrent: false } },
  { label: 'completed failure', query: { ...loaded, status: 'error' as const } },
  {
    label: 'failed data retained during a retry',
    query: { ...loaded, status: 'loading' as const, error: 'Prior read failed.' },
  },
])('rejects $label without treating retained data as healthy evidence', ({ query }) => {
  expect(hasHealthyCurrentQueryData(query)).toBe(false)
})
