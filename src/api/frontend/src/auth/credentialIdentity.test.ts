// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { afterEach, expect, it, vi } from 'vitest'
const { webcrypto } = await vi.importActual<{ webcrypto: Crypto }>('node:crypto')
import { credentialExpiresAt, fingerprintCredential } from './credentialIdentity'

afterEach(() => vi.unstubAllGlobals())
it('uses stable native SHA-256 identity without returning the bearer and extracts only its expiration horizon', async () => {
  vi.stubGlobal('crypto', webcrypto)
  const first = await fingerprintCredential('fictional-first')
  expect(first).toMatch(/^[a-f0-9]{64}$/)
  expect(await fingerprintCredential('fictional-first')).toBe(first)
  expect(await fingerprintCredential('fictional-second')).not.toBe(first)
  expect(credentialExpiresAt(`header.${btoa(JSON.stringify({ exp: 12345 }))}.signature`)).toBe(
    12345000,
  )
  expect(credentialExpiresAt('opaque-invalid-credential')).toBeUndefined()
})
it('declines recovery identity when native hashing is unavailable', async () => {
  vi.stubGlobal('crypto', { subtle: undefined })
  expect(await fingerprintCredential('fictional-first')).toBeUndefined()
})
