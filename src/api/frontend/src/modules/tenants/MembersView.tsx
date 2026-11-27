// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useEffect, useState } from 'react'
import type { TenantRole } from '../../api/types'
import { useAppServices } from '../../shell/AppContext'
import { useScopedQuery } from '../../data/useScopedQuery'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import {
  Button,
  ConfirmationDialog,
  DataTable,
  EmptyState,
  Field,
  Notice,
  StatusBadge,
} from '../../ui/components'
import {
  changeTenantInvitation,
  fetchTenantInvitations,
  fetchTenantMembers,
  inviteTenantMember,
  removeTenantMember,
  updateTenantMember,
} from './api'
import type { TenantInvitation, TenantMember } from './types'

const roles: TenantRole[] = ['Viewer', 'Member', 'Admin', 'Owner']
function RoleActions({
  current,
  owner,
  disabled,
  label,
  save,
}: {
  current: TenantRole
  owner: boolean
  disabled: boolean
  label: string
  save: (role: TenantRole) => void
}) {
  const [role, setRole] = useState(current)
  return (
    <>
      <select
        aria-label={label}
        value={role}
        disabled={disabled}
        onChange={(event) => setRole(event.target.value as TenantRole)}
      >
        {roles
          .filter((item) => owner || item !== 'Owner')
          .map((item) => (
            <option key={item}>{item}</option>
          ))}
      </select>
      <Button disabled={disabled || role === current} onClick={() => save(role)}>
        Change role
      </Button>
    </>
  )
}
export function MembersView() {
  const app = useAppServices()
  if (!app.tenant.active) return <EmptyState title="Select an available tenant." />
  return (
    <MembersContent
      key={`${app.auth.userId}:${app.tenant.active.id}:${app.tenant.active.role}:${app.auth.isPlatformAdmin}`}
    />
  )
}
function MembersContent() {
  const { api, auth, tenant, notifications, navigation } = useAppServices()
  const id = tenant.active!.id
  const owner = tenant.active!.role === 'Owner'
  const canManage = owner || tenant.active!.role === 'Admin'
  const canManageMembers = canManage || auth.isPlatformAdmin
  const canManageOwners = owner || auth.isPlatformAdmin
  const origin = useProviderInvalidation(api)
  const mutation = useScopedMutation(
    `${origin.identity}:${tenant.active!.role}:${auth.isPlatformAdmin}`,
  )
  const members = useScopedQuery<TenantMember[]>({
    scope: [api.baseUrl, id],
    key: ['tenant-members', 'members'],
    realtime: [{ topic: 'tenant-members', tenantId: id }],
    interval: 60000,
    load: (signal) => fetchTenantMembers(api.baseUrl, id, origin.credential(), signal),
  })
  const invitations = useScopedQuery<TenantInvitation[]>({
    scope: [api.baseUrl, id],
    key: ['tenant-members', 'invitations'],
    enabled: canManage,
    realtime: [{ topic: 'tenant-members', tenantId: id }],
    interval: 60000,
    load: (signal) => fetchTenantInvitations(api.baseUrl, id, origin.credential(), signal),
  })
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<TenantRole>('Viewer')
  const [removal, setRemoval] = useState<TenantMember | null>(null)
  const healthy = hasHealthyCurrentQueryData(members)
  const inviteHealthy = hasHealthyCurrentQueryData(invitations)
  const registerUnsavedChanges = navigation.registerUnsavedChanges
  useEffect(
    () => (email.trim() ? registerUnsavedChanges('member invitation') : undefined),
    [email, registerUnsavedChanges],
  )
  const { key, session } = origin
  useEffect(() => {
    if (canManage) return
    const queryKey = key(['tenant-members', 'invitations'])
    void session.client
      .cancelQueries({ queryKey })
      .then(() => session.client.removeQueries({ queryKey }))
  }, [canManage, key, session])
  const afterWrite = async () => {
    if (!origin.isCurrent()) return
    await origin.invalidate([['tenant-members']])
    if (origin.isCurrent()) notifications.publish('Membership updated.')
  }
  const submit = () => {
    if (!canManage || !healthy || !inviteHealthy) return
    const command = { email: email.trim(), role }
    void mutation
      .run(
        (signal) =>
          inviteTenantMember(
            api.baseUrl,
            id,
            command.email,
            command.role,
            origin.credential(),
            signal,
          ),
        async () => {
          if (!origin.isCurrent()) return
          setEmail('')
          setRole('Viewer')
          await afterWrite()
        },
      )
      .catch(() => {})
  }
  const changeMember = (member: TenantMember, next: TenantRole | 'remove') => {
    if (
      !canManageMembers ||
      !healthy ||
      (!canManageOwners && (member.role === 'Owner' || next === 'Owner'))
    )
      return
    void mutation
      .run(
        async (signal) => {
          const current = await members.refresh()
          if (!origin.isCurrent() || !current)
            throw new Error('Refresh members before changing access.')
          const selected = current.find((item) => item.id === member.id)
          if (!selected || selected.role !== member.role)
            throw new Error('This membership changed. Review the current role before continuing.')
          if (
            selected.role === 'Owner' &&
            current.filter((item) => item.role === 'Owner').length === 1
          )
            throw new Error('A tenant must retain at least one owner.')
          return next === 'remove'
            ? removeTenantMember(api.baseUrl, id, member.id, origin.credential(), signal)
            : updateTenantMember(api.baseUrl, id, member.id, next, origin.credential(), signal)
        },
        async () => {
          if (origin.isCurrent()) {
            setRemoval(null)
            await afterWrite()
          }
        },
      )
      .catch(() => {})
  }
  const changeInvitation = (
    invitation: TenantInvitation,
    action: 'resend' | 'revoke' | TenantRole,
  ) => {
    if (
      !canManage ||
      !inviteHealthy ||
      (!owner && (invitation.role === 'Owner' || action === 'Owner'))
    )
      return
    const command = {
      id: invitation.id,
      revision: invitation.revision,
      role: invitation.role,
      action,
    }
    void mutation
      .run(async (signal) => {
        const current = await invitations.refresh()
        if (!origin.isCurrent() || !current)
          throw new Error('Refresh invitations before changing them.')
        const selected = current.find((item) => item.id === command.id)
        if (
          !selected ||
          selected.revision !== command.revision ||
          selected.role !== command.role ||
          selected.status !== 'Pending'
        )
          throw new Error('This invitation changed. Review it before continuing.')
        return changeTenantInvitation(
          api.baseUrl,
          id,
          command.id,
          command.action,
          command.revision,
          origin.credential(),
          signal,
        )
      }, afterWrite)
      .catch(() => {})
  }
  const rows = members.resource ?? []
  return (
    <section>
      <h1>Members</h1>
      {members.error && <Notice tone="error">{members.error}</Notice>}
      {Boolean(mutation.error) && (
        <Notice tone="error">
          {mutation.error instanceof Error
            ? mutation.error.message
            : 'The action could not be completed.'}
        </Notice>
      )}
      {members.isInitialLoading ? (
        <EmptyState title="Loading members…" />
      ) : (
        <DataTable
          label="Tenant members"
          items={rows}
          itemKey={(member) => member.id}
          rowTestId={(member) => `tenant-member-${member.id}`}
          columns={[
            { id: 'email', label: 'Email address', render: (member) => member.email },
            { id: 'role', label: 'Role', render: (member) => member.role },
            {
              id: 'default',
              label: 'Default tenant',
              render: (member) => (member.isDefault ? <StatusBadge>Default</StatusBadge> : '—'),
            },
            {
              id: 'actions',
              label: 'Actions',
              render: (member) => {
                const lastOwner =
                  member.role === 'Owner' &&
                  rows.filter((item) => item.role === 'Owner').length === 1
                return canManageMembers && (canManageOwners || member.role !== 'Owner') ? (
                  <div className="row-actions">
                    <RoleActions
                      key={`${member.id}:${member.role}`}
                      current={member.role}
                      owner={canManageOwners}
                      label={`Role for ${member.email}`}
                      disabled={mutation.busy || !healthy || lastOwner}
                      save={(next) => changeMember(member, next)}
                    />
                    <Button
                      variant="danger"
                      disabled={mutation.busy || !healthy || lastOwner}
                      onClick={() => setRemoval(member)}
                    >
                      Remove
                    </Button>
                    {lastOwner && <span>Last owner</span>}
                  </div>
                ) : (
                  '—'
                )
              },
            },
          ]}
        />
      )}
      {canManage && (
        <>
          <h2>Invite member</h2>
          <form
            onSubmit={(event) => {
              event.preventDefault()
              submit()
            }}
          >
            <Field label="Email address">
              <input
                type="email"
                required
                maxLength={254}
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                disabled={mutation.busy}
              />
            </Field>
            <Field label="Role">
              <select
                value={role}
                onChange={(event) => setRole(event.target.value as TenantRole)}
                disabled={mutation.busy}
              >
                {roles
                  .filter((item) => owner || item !== 'Owner')
                  .map((item) => (
                    <option key={item}>{item}</option>
                  ))}
              </select>
            </Field>
            <Button
              type="submit"
              variant="primary"
              disabled={mutation.busy || !healthy || !inviteHealthy || !email.trim()}
            >
              Send invitation
            </Button>
          </form>
          <h2>Invitations</h2>
          {invitations.error && <Notice tone="error">{invitations.error}</Notice>}
          <DataTable
            label="Tenant invitations"
            items={invitations.resource ?? []}
            itemKey={(invitation) => invitation.id}
            rowTestId={(invitation) => `tenant-invitation-${invitation.id}`}
            columns={[
              { id: 'email', label: 'Email address', render: (invitation) => invitation.email },
              { id: 'role', label: 'Role', render: (invitation) => invitation.role },
              {
                id: 'status',
                label: 'Status',
                render: (invitation) =>
                  invitation.status === 'Pending' &&
                  Date.parse(invitation.expiresAtUtc) <= Date.now()
                    ? 'Expired'
                    : invitation.status,
              },
              {
                id: 'delivery',
                label: 'Delivery',
                render: (invitation) => (
                  <StatusBadge tone={invitation.deliveryStatus === 'Failed' ? 'danger' : 'neutral'}>
                    {invitation.deliveryStatus}
                  </StatusBadge>
                ),
              },
              {
                id: 'expires',
                label: 'Expires',
                render: (invitation) => new Date(invitation.expiresAtUtc).toLocaleString(),
              },
              {
                id: 'actions',
                label: 'Actions',
                render: (invitation) =>
                  invitation.status === 'Pending' && (owner || invitation.role !== 'Owner') ? (
                    <div className="row-actions">
                      <Button
                        disabled={mutation.busy || !inviteHealthy}
                        onClick={() => changeInvitation(invitation, 'resend')}
                      >
                        Resend
                      </Button>
                      <Button
                        disabled={mutation.busy || !inviteHealthy}
                        onClick={() => changeInvitation(invitation, 'revoke')}
                      >
                        Revoke
                      </Button>
                      <RoleActions
                        key={`${invitation.id}:${invitation.revision}:${invitation.role}`}
                        current={invitation.role}
                        owner={owner}
                        label={`Invitation role for ${invitation.email}`}
                        disabled={mutation.busy || !inviteHealthy}
                        save={(next) => changeInvitation(invitation, next)}
                      />
                    </div>
                  ) : (
                    '—'
                  ),
              },
            ]}
          />
        </>
      )}
      {removal && (
        <ConfirmationDialog
          title="Remove tenant member?"
          confirmLabel="Remove member"
          busy={mutation.busy}
          onCancel={() => setRemoval(null)}
          onConfirm={() => changeMember(removal, 'remove')}
        >
          <p>{removal.email} will lose access to this tenant. Their account remains available.</p>
        </ConfirmationDialog>
      )}
    </section>
  )
}
