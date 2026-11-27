// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// Identity evidence stays local to the authenticated controller lifetime. Never retain a
// historical bearer or publish this fingerprint in query keys, storage or tab messages.
export async function fingerprintCredential(credential: string): Promise<string | undefined> {
  try {
    if (!globalThis.crypto?.subtle) return undefined
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(credential))
    return [...new Uint8Array(digest)].map((value) => value.toString(16).padStart(2, '0')).join('')
  } catch {
    return undefined
  }
}
export function credentialExpiresAt(credential: string): number | undefined {
  try {
    const payload = credential.split('.')[1]
    const claims = JSON.parse(atob(payload.replaceAll('-', '+').replaceAll('_', '/'))) as {
      exp?: unknown
    }
    return typeof claims.exp === 'number' && Number.isFinite(claims.exp)
      ? claims.exp * 1000
      : undefined
  } catch {
    return undefined
  }
}
