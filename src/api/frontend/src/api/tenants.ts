import type { Tenant, TenantRole } from '../types'
import { buildHeaders, fetchJson } from './transport'

type TenantResponse = Omit<Tenant, 'role'> & {
  role: unknown
}

const tenantRoles = new Set<TenantRole>(['Viewer', 'Member', 'Admin', 'Owner'])

export function normalizeTenantRole(role: unknown): TenantRole {
  if (typeof role === 'string' && tenantRoles.has(role as TenantRole)) return role as TenantRole

  throw new Error(`Unknown tenant role value: ${String(role)}`)
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
