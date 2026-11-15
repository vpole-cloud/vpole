import type { TenantRole } from '../../api/types'

export interface TenantMember {
  id: string
  tenantId: string
  userId: string
  email: string
  role: TenantRole
  isDefault: boolean
}
export interface TenantInvitation {
  revision: number
  id: string
  tenantId: string
  email: string
  role: TenantRole
  expiresAtUtc: string
  status: 'Pending' | 'Revoked' | 'Accepted'
  deliveryStatus: 'Pending' | 'Sent' | 'Failed'
}
export interface InvitationPreview {
  tenantName: string
  email: string
  role: TenantRole
  expiresAtUtc: string
}
export interface InvitationAccepted {
  tenantId: string
  defaultTenantId: string
}
