import type { Tenant, TenantRole } from '../types'
import { buildHeaders, fetchJson } from './transport'

type TenantResponse = Omit<Tenant, 'role'> & {
  role: TenantRole | number
}

const tenantRoles: Record<number, TenantRole> = {
  0: 'Viewer',
  1: 'Member',
  2: 'Admin',
  3: 'Owner',
}

export function normalizeTenantRole(role: TenantRole | number): TenantRole {
  if (typeof role === 'string') return role

  const normalized = tenantRoles[role]
  if (normalized) return normalized

  throw new Error(`Unknown tenant role value: ${role}`)
}

export async function fetchTenants(apiBaseUrl: string, token?: string, signal?: AbortSignal) {
  const tenants = await fetchJson<TenantResponse[]>(`${apiBaseUrl}/api/v1/tenants`, {
    headers: buildHeaders(token),
    signal,
  })

  return tenants.map((tenant) => ({
    ...tenant,
    role: normalizeTenantRole(tenant.role),
  }))
}
