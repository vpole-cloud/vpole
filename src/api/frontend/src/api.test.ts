import { describe, expect, it } from 'vitest'
import { normalizeTenantRole } from './api'

describe('tenant role normalization', () => {
  it('maps the numeric API enum to the frontend role contract', () => {
    expect(normalizeTenantRole(3)).toBe('Owner')
    expect(normalizeTenantRole(1)).toBe('Member')
  })

  it('preserves a string role and rejects an unknown enum value', () => {
    expect(normalizeTenantRole('Admin')).toBe('Admin')
    expect(() => normalizeTenantRole(99)).toThrow('Unknown tenant role value: 99')
  })
})
