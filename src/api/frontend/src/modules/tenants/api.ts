import { buildHeaders, fetchJson, sendJson, sendWithoutResponse } from '../../api/transport'
import type { TenantRole } from '../../api/types'
import type { InvitationAccepted, InvitationPreview, TenantInvitation, TenantMember } from './types'

const tenantRoute = (baseUrl: string, tenantId: string) =>
  `${baseUrl}/api/v1/tenants/${encodeURIComponent(tenantId)}`
export const fetchTenantMembers = (
  baseUrl: string,
  tenantId: string,
  token?: string,
  signal?: AbortSignal,
) =>
  fetchJson<TenantMember[]>(`${tenantRoute(baseUrl, tenantId)}/members`, {
    headers: buildHeaders(token),
    signal,
  })
export const fetchTenantInvitations = (
  baseUrl: string,
  tenantId: string,
  token?: string,
  signal?: AbortSignal,
) =>
  fetchJson<TenantInvitation[]>(`${tenantRoute(baseUrl, tenantId)}/invitations`, {
    headers: buildHeaders(token),
    signal,
  })
export const inviteTenantMember = (
  baseUrl: string,
  tenantId: string,
  email: string,
  role: TenantRole,
  token?: string,
  signal?: AbortSignal,
) =>
  sendJson<TenantInvitation>(`${tenantRoute(baseUrl, tenantId)}/invitations`, {
    method: 'POST',
    headers: buildHeaders(token),
    body: JSON.stringify({ email, role }),
    signal,
  })
export const changeTenantInvitation = (
  baseUrl: string,
  tenantId: string,
  invitationId: string,
  action: 'resend' | 'revoke' | TenantRole,
  expectedRevision: number,
  token?: string,
  signal?: AbortSignal,
) => {
  const roleUpdate = action !== 'resend' && action !== 'revoke'
  return sendJson<TenantInvitation>(
    `${tenantRoute(baseUrl, tenantId)}/invitations/${encodeURIComponent(invitationId)}${roleUpdate ? '' : `/${action}`}`,
    {
      method: roleUpdate ? 'PUT' : 'POST',
      headers: buildHeaders(token),
      body: JSON.stringify(roleUpdate ? { role: action, expectedRevision } : { expectedRevision }),
      signal,
    },
  )
}
export const updateTenantMember = (
  baseUrl: string,
  tenantId: string,
  memberId: string,
  role: TenantRole,
  token?: string,
  signal?: AbortSignal,
) =>
  sendJson<TenantMember>(
    `${tenantRoute(baseUrl, tenantId)}/members/${encodeURIComponent(memberId)}`,
    {
      method: 'PUT',
      headers: buildHeaders(token),
      body: JSON.stringify({ role }),
      signal,
    },
  )
export const removeTenantMember = (
  baseUrl: string,
  tenantId: string,
  memberId: string,
  token?: string,
  signal?: AbortSignal,
) =>
  sendWithoutResponse(`${tenantRoute(baseUrl, tenantId)}/members/${encodeURIComponent(memberId)}`, {
    method: 'DELETE',
    headers: buildHeaders(token),
    signal,
  })
export function invitationAction<T>(
  baseUrl: string,
  action: 'preview' | 'onboarding' | 'accept',
  invitationToken: string,
  token?: string,
  signal?: AbortSignal,
) {
  return sendJson<T>(`${baseUrl}/api/v1/tenant-invitations/${action}`, {
    method: 'POST',
    headers: buildHeaders(token),
    body: JSON.stringify({ token: invitationToken }),
    signal,
  })
}
export const previewTenantInvitation = (baseUrl: string, secret: string, signal?: AbortSignal) =>
  invitationAction<InvitationPreview>(baseUrl, 'preview', secret, undefined, signal)
export const acceptTenantInvitation = (
  baseUrl: string,
  secret: string,
  token?: string,
  signal?: AbortSignal,
) => invitationAction<InvitationAccepted>(baseUrl, 'accept', secret, token, signal)
