import { useCallback, useEffect, useRef, useState } from 'react'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { canConfirmBackup, backupConfirmationText } from './confirmations'
import type { BackupConfirmation } from './confirmations'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useApiScope } from '../../data/useApiScope'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { Button, DataTable, Field, Notice, ConfirmationDialog } from '../../ui/components'
import { backupApi, fetchBackupCredentialChoices, fetchBackupClusterChoices } from './api'
import {
  backupPolicyConfiguration,
  isBackupPolicyDirty,
  reapplyBackupPolicyChanges,
} from './configurationModel'
import type { BackupConfigurationVersion } from './configurationModel'
import type {
  BackupArchive,
  BackupDestinationInput,
  BackupPolicy,
  BackupPolicyInput,
  BackupRestoreInput,
  BackupRestoreSelection,
} from './types'

type BackupEditor = { kind: 'destination' } | { kind: 'policy'; policyId: string | null } | null

export function BackupsView() {
  const { api, tenant, auth } = useAppServices()
  return (
    <BackupWorkspace
      key={`${api.baseUrl}:${api.tenantId}:${tenant.canManage}:${tenant.active?.role}:${auth.isPlatformAdmin}`}
    />
  )
}

function BackupWorkspace() {
  const { api, tenant, auth, notifications, navigation } = useAppServices()
  const origin = useApiScope(api)
  const client = backupApi(api.baseUrl, origin.credential, api.tenantId)
  const scope = [api.baseUrl, api.tenantId]
  const destinations = useScopedQuery({
    scope,
    key: ['backups', 'destinations'],
    load: client.destinations,
  })
  const policies = useScopedQuery({ scope, key: ['backups', 'policies'], load: client.policies })
  const archives = useScopedQuery({ scope, key: ['backups', 'archives'], load: client.archives })
  const operations = useScopedQuery({
    scope,
    key: ['backups', 'operations'],
    interval: 10000,
    load: client.operations,
  })
  const [archiveId, setArchiveId] = useState('')
  const archive = archives.resource?.find((item) => item.id === archiveId)
  const catalog = useScopedQuery({
    scope,
    key: ['backups', 'catalog', archiveId],
    enabled: Boolean(archive),
    load: (signal) => client.catalog(archiveId, signal),
  })
  const mutation = useScopedMutation(origin.identity)
  const manualKeys = useRef(new Map<string, string>())
  const [confirmation, setConfirmation] = useState<BackupConfirmation | null>(null)
  const restoreKeys = useRef(new Map<string, string>())
  const [newName, setNewName] = useState('')
  const [targetTime, setTargetTime] = useState('')
  const [restoreStorage, setRestoreStorage] = useState('')
  const [editor, setEditor] = useState<BackupEditor>(null)
  const [pendingEditor, setPendingEditor] = useState<{ next: BackupEditor } | null>(null)
  const dirtyEditor = useRef(false)
  const editorDirty = useCallback((dirty: boolean) => {
    dirtyEditor.current = dirty
  }, [])
  const editingPolicy =
    editor?.kind === 'policy' && editor.policyId
      ? policies.resource?.find((item) => item.id === editor.policyId)
      : undefined
  const changeEditor = (next: BackupEditor) => {
    dirtyEditor.current = false
    setEditor(next)
    setPendingEditor(null)
  }
  const requestEditor = (next: BackupEditor) => {
    if (mutation.busy) return
    if (dirtyEditor.current) setPendingEditor({ next })
    else changeEditor(next)
  }
  const canRestore = auth.isPlatformAdmin || ['Owner', 'Admin'].includes(tenant.active?.role ?? '')
  const refresh = async () => {
    await Promise.all([
      destinations.refreshAfterWrite(),
      policies.refreshAfterWrite(),
      archives.refreshAfterWrite(),
      operations.refreshAfterWrite(),
    ])
  }
  const run = (action: (signal: AbortSignal) => Promise<unknown>, done?: () => void) => {
    void mutation
      .run(action, async () => {
        if (!origin.isCurrent()) return
        done?.()
        notifications.publish('Request accepted.')
        await refresh()
      })
      .catch(() => {})
  }
  const restore = (source: BackupArchive, selection: BackupRestoreSelection) => {
    if (!newName.trim()) return
    const body: BackupRestoreInput = {
      ...selection,
      name: newName.trim(),
      confirmed: true,
      idempotencyKey: crypto.randomUUID(),
      ...(restoreStorage && { storageGiB: Number(restoreStorage) }),
    }
    const key = JSON.stringify([
      source.id,
      body.name,
      body.backupId,
      body.targetTime,
      body.storageGiB,
    ])
    body.idempotencyKey = restoreKeys.current.get(key) ?? body.idempotencyKey
    restoreKeys.current.set(key, body.idempotencyKey)
    if (body.backupId !== undefined) {
      const copy = catalog.resource?.find((item) => item.backupId === body.backupId)
      if (copy) setConfirmation({ kind: 'restoreCopy', archive: source, command: body, copy })
    } else setConfirmation({ kind: 'restoreTime', archive: source, command: body })
  }
  const confirmationAllowed =
    confirmation &&
    origin.isCurrent() &&
    canConfirmBackup(confirmation, {
      canManage: tenant.canManage,
      canRestore,
      archiveId,
      destinations: hasHealthyCurrentQueryData(destinations) ? destinations.resource : null,
      policies: hasHealthyCurrentQueryData(policies) ? policies.resource : null,
      archives: hasHealthyCurrentQueryData(archives) ? archives.resource : null,
      catalog: hasHealthyCurrentQueryData(catalog) ? catalog.resource : null,
    })
  const confirm = () => {
    if (!confirmation || !confirmationAllowed || mutation.busy) return
    const selected = confirmation
    run(
      (signal) => {
        switch (selected.kind) {
          case 'disconnect':
            return client.deleteDestination(selected.destination, signal)
          case 'deletePolicy':
            return client.deletePolicy(selected.policy, signal)
          case 'forgetArchive':
            return client.forgetArchive(selected.archive.id, signal)
          case 'restoreCopy':
          case 'restoreTime':
            return client.restore(selected.archive.id, selected.command, signal)
        }
      },
      () => {
        if (selected.kind === 'restoreCopy' || selected.kind === 'restoreTime') {
          for (const [key, value] of restoreKeys.current)
            if (value === selected.command.idempotencyKey) restoreKeys.current.delete(key)
        }
        setConfirmation(null)
      },
    )
  }
  const confirmationText = confirmation ? backupConfirmationText(confirmation) : null

  return (
    <section className="panel">
      <header className="ui-page-header">
        <div>
          <h1>Backups</h1>
        </div>
        <Button onClick={() => void refresh()}>Refresh</Button>
      </header>
      {[destinations.error, policies.error, archives.error, operations.error]
        .filter(Boolean)
        .map((error, index) => (
          <Notice key={index} tone="error">
            {error}
          </Notice>
        ))}
      {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
      <h2>Destinations</h2>
      {tenant.canManage && (
        <Button
          onClick={() => {
            requestEditor(editor?.kind === 'destination' ? null : { kind: 'destination' })
          }}
        >
          Connect storage
        </Button>
      )}
      {editor?.kind === 'destination' && (
        <DestinationForm
          busy={mutation.busy}
          onDirtyChange={editorDirty}
          onSave={(input) =>
            run(
              (signal) => client.saveDestination(input, signal),
              () => changeEditor(null),
            )
          }
        />
      )}
      <DataTable
        label="Backup destinations"
        rowTestId={(item) => `backup-destination-${item.id}`}
        items={destinations.resource ?? []}
        itemKey={(item) => item.id}
        columns={[
          { id: 'name', label: 'Name', render: (item) => item.name },
          {
            id: 'location',
            label: 'Location',
            render: (item) => `${item.endpoint} · ${item.bucket}/${item.prefix}`,
          },
          {
            id: 'actions',
            label: 'Actions',
            render: (item) =>
              tenant.canManage && (
                <div className="row-actions">
                  <Button
                    disabled={mutation.busy || !hasHealthyCurrentQueryData(destinations) || Boolean(item.gitOps)}
                    onClick={() =>
                      setConfirmation({
                        kind: 'disconnect',
                        destination: item,
                      })
                    }
                  >
                    Disconnect
                  </Button>
                </div>
              ),
          },
        ]}
      />
      <h2>Policies</h2>
      {tenant.canManage && (
        <Button
          onClick={() => {
            requestEditor(
              editor?.kind === 'policy' && editor.policyId === null
                ? null
                : { kind: 'policy', policyId: null },
            )
          }}
        >
          Add policy
        </Button>
      )}
      {editor?.kind === 'policy' && (
        <PolicyForm
          key={editor.policyId ?? 'new'}
          current={editingPolicy}
          busy={mutation.busy}
          onDirtyChange={editorDirty}
          destinations={(destinations.resource ?? []).map((item) => ({
            id: item.id,
            name: item.name,
          }))}
          onSave={(input, baseline) =>
            run(
              (signal) => client.savePolicy(input, signal, baseline),
              () => changeEditor(null),
            )
          }
        />
      )}
      <DataTable
        label="Backup policies"
        rowTestId={(item) => `backup-policy-${item.id}`}
        items={policies.resource ?? []}
        itemKey={(item) => item.id}
        columns={[
          { id: 'name', label: 'Policy', render: (item) => item.name },
          {
            id: 'schedule',
            label: 'Schedule (UTC)',
            render: (item) =>
              `${item.schedule}${item.deleting ? ' · Stopping' : item.suspended ? ' · Paused' : ''}`,
          },
          {
            id: 'next',
            label: 'Next backup',
            render: (item) =>
              item.nextScheduledAt
                ? `${new Date(item.nextScheduledAt).toISOString()} · ${new Date(item.nextScheduledAt).toLocaleString()}`
                : '—',
          },
          {
            id: 'last-base',
            label: 'Last successful base backup',
            render: (item) =>
              item.lastSuccessfulBackupAt
                ? new Date(item.lastSuccessfulBackupAt).toLocaleString()
                : '—',
          },
          {
            id: 'health',
            label: 'Health',
            render: (item) => item.failureCode ?? item.walArchivingStatus ?? '—',
          },
          {
            id: 'actions',
            label: 'Actions',
            render: (item) =>
              tenant.canManage && (
                <div className="row-actions">
                  <Button
                    disabled={
                      mutation.busy ||
                      !hasHealthyCurrentQueryData(policies) ||
                      Boolean(item.deleting) ||
                      Boolean(item.gitOps)
                    }
                    onClick={() => {
                      if (editor?.kind !== 'policy' || editor.policyId !== item.id)
                        requestEditor({ kind: 'policy', policyId: item.id })
                    }}
                  >
                    Edit
                  </Button>
                  <Button
                    disabled={mutation.busy || !hasHealthyCurrentQueryData(policies) || Boolean(item.deleting)}
                    onClick={() => {
                      const id = manualKeys.current.get(item.id) ?? crypto.randomUUID()
                      manualKeys.current.set(item.id, id)
                      run(
                        (signal) => client.run(item.id, id, signal),
                        () => {
                          manualKeys.current.delete(item.id)
                        },
                      )
                    }}
                  >
                    Back up now
                  </Button>
                  <Button
                    disabled={
                      mutation.busy ||
                      !hasHealthyCurrentQueryData(policies) ||
                      Boolean(item.deleting) ||
                      Boolean(item.gitOps)
                    }
                    onClick={() =>
                      run((signal) =>
                        client.savePolicy({ ...item, suspended: !item.suspended }, signal, item),
                      )
                    }
                  >
                    {item.suspended ? 'Resume scheduled backups' : 'Pause scheduled backups'}
                  </Button>
                  <Button
                    disabled={
                      mutation.busy ||
                      !hasHealthyCurrentQueryData(policies) ||
                      Boolean(item.deleting) ||
                      Boolean(item.gitOps)
                    }
                    onClick={() =>
                      setConfirmation({
                        kind: 'deletePolicy',
                        policy: item,
                      })
                    }
                  >
                    Delete
                  </Button>
                </div>
              ),
          },
        ]}
      />
      <h2>Available backups</h2>
      <Field label="Source archive">
        <select
          className="field__control"
          value={archiveId}
          onChange={(event) => {
            setArchiveId(event.target.value)
            setNewName('')
            setTargetTime('')
            setRestoreStorage('')
            setConfirmation(null)
          }}
        >
          <option value="">Select an archive</option>
          {archives.resource?.map((item) => (
            <option key={item.id} value={item.id}>
              {item.resourceName} · {item.serverName}
              {item.active ? '' : ' · Retained'}
            </option>
          ))}
        </select>
      </Field>
      {archive && (
        <>
          {canRestore && !archive.active && (
            <Button
              disabled={mutation.busy}
              onClick={() =>
                setConfirmation({
                  kind: 'forgetArchive',
                  archive,
                })
              }
            >
              Forget retired archive
            </Button>
          )}
          <Button onClick={() => void catalog.refresh()}>Refresh backup list</Button>
          {catalog.error && <Notice tone="error">{catalog.error}</Notice>}
          {catalog.status === 'loading' && <p>Loading backups…</p>}
          {catalog.status === 'success' && catalog.resource?.length === 0 && (
            <p>No completed backups in this archive.</p>
          )}
          {canRestore && (
            <Field label="New cluster name">
              <input
                className="field__control"
                value={newName}
                onChange={(event) => setNewName(event.target.value)}
                required
              />
            </Field>
          )}
          {canRestore && (
            <Field label="Storage per instance (GiB, optional)">
              <input
                className="field__control"
                type="number"
                min={archive.storageGiB ?? 1}
                step="1"
                value={restoreStorage}
                onChange={(event) => setRestoreStorage(event.target.value)}
              />
            </Field>
          )}
          <DataTable
            label="Completed backups"
            rowTestId={(item) => `backup-copy-${item.backupId}`}
            items={catalog.resource ?? []}
            itemKey={(item) => item.backupId}
            columns={[
              { id: 'id', label: 'Backup', render: (item) => item.backupId },
              {
                id: 'completed',
                label: 'Completed',
                render: (item) => new Date(item.completedAt).toLocaleString(),
              },
              {
                id: 'restore',
                label: 'Actions',
                render: (item) =>
                  canRestore && (
                    <Button
                      disabled={
                        mutation.busy ||
                        !newName.trim() ||
                        !hasHealthyCurrentQueryData(catalog) ||
                        Boolean(catalog.error)
                      }
                      onClick={() => restore(archive, { backupId: item.backupId })}
                    >
                      Restore
                    </Button>
                  ),
              },
            ]}
          />
          {canRestore && archive.supportsPointInTimeRecovery && (
            <form
              onSubmit={(event) => {
                event.preventDefault()
                if (targetTime) restore(archive, { targetTime: new Date(targetTime).toISOString() })
              }}
            >
              <Field label="Recovery time (local)">
                <input
                  className="field__control"
                  type="datetime-local"
                  step="1"
                  required
                  value={targetTime}
                  onChange={(event) => setTargetTime(event.target.value)}
                />
              </Field>
              <Button
                type="submit"
                disabled={
                  mutation.busy ||
                  !newName.trim() ||
                  !targetTime ||
                  !hasHealthyCurrentQueryData(catalog) ||
                  Boolean(catalog.error)
                }
              >
                Restore to chosen time
              </Button>
            </form>
          )}
        </>
      )}
      <h2>Operations</h2>
      <DataTable
        label="Backup and restore operations"
        rowTestId={(item) => `backup-operation-${item.id}`}
        items={operations.resource ?? []}
        itemKey={(item) => item.id}
        columns={[
          { id: 'kind', label: 'Operation', render: (item) => item.kind },
          { id: 'state', label: 'State', render: (item) => item.failureCode ?? item.state },
          {
            id: 'time',
            label: 'Started',
            render: (item) => new Date(item.createdAt).toLocaleString(),
          },
          {
            id: 'resource',
            label: 'Restored resource',
            render: (item) => {
              const source = archives.resource?.find((entry) => entry.id === item.archiveId)
              return item.state === 'Succeeded' && item.newResourceId && source ? (
                <a
                  href={navigation.resourceHref({
                    resourceTypeId: source.resourceType,
                    resourceId: item.newResourceId,
                  })}
                >
                  Open resource
                </a>
              ) : null
            },
          },
        ]}
      />
      {pendingEditor && (
        <ConfirmationDialog
          title="Discard unsaved changes?"
          confirmLabel="Discard changes"
          onCancel={() => setPendingEditor(null)}
          onConfirm={() => changeEditor(pendingEditor.next)}
        >
          The current form has unsaved changes.
        </ConfirmationDialog>
      )}
      {confirmation && confirmationText && (
        <ConfirmationDialog
          title={confirmationText.title}
          confirmLabel="Confirm"
          busy={mutation.busy}
          onCancel={() => setConfirmation(null)}
          confirmDisabled={!confirmationAllowed}
          onConfirm={confirm}
        >
          {confirmationText.description}
          {!confirmationAllowed && (
            <Notice tone="error">
              The selected data changed or could not be refreshed. Cancel and review the current
              selection.
            </Notice>
          )}
        </ConfirmationDialog>
      )}
    </section>
  )
}

function DestinationForm({
  busy,
  onDirtyChange,
  onSave,
}: {
  busy: boolean
  onSave: (input: BackupDestinationInput) => void
  onDirtyChange: (dirty: boolean) => void
}) {
  const [input, setInput] = useState<BackupDestinationInput>({
    name: '',
    resourceGroupId: '',
    isolationScopeId: '',
    endpoint: '',
    bucket: '',
    prefix: '',
    credentialSecretId: '',
    accessKeyIdKey: 'ACCESS_KEY_ID',
    secretAccessKeyKey: 'ACCESS_SECRET_KEY',
  })
  const [baseline] = useState(input)
  const dirty = JSON.stringify(input) !== JSON.stringify(baseline)
  useUnsavedChanges(dirty, 'Backup destination')
  useEffect(() => {
    onDirtyChange(dirty)
    return () => onDirtyChange(false)
  }, [dirty, onDirtyChange])
  const { api } = useAppServices()
  const origin = useApiScope(api)
  const credentials = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['backups', 'credential-choices'],
    load: (signal) =>
      fetchBackupCredentialChoices(api.baseUrl, origin.credential, api.tenantId, signal),
  })
  const labels: Partial<Record<keyof BackupDestinationInput, string>> = {
    name: 'Name',
    endpoint: 'S3 endpoint',
    bucket: 'Bucket',
    prefix: 'Prefix',
    accessKeyIdKey: 'Access key name in Secret',
    secretAccessKeyKey: 'Secret key name in Secret',
  }
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        onSave(input)
      }}
    >
      <fieldset disabled={busy} className="form-grid">
        {credentials.error && <Notice tone="error">{credentials.error}</Notice>}
        <Field label="Storage credentials">
          <select
            className="field__control"
            required
            value={input.credentialSecretId}
            onChange={(event) => {
              const secret = credentials.resource?.find((item) => item.id === event.target.value)
              setInput({
                ...input,
                credentialSecretId: secret?.id ?? '',
                resourceGroupId: secret?.resourceGroupId ?? '',
                isolationScopeId: secret?.isolationScopeId ?? '',
              })
            }}
          >
            <option value="">Select a Secret</option>
            {credentials.resource
              ?.filter((item) => !item.managedBy)
              .map((item) => (
                <option key={item.id} value={item.id}>
                  {item.name}
                </option>
              ))}
          </select>
        </Field>
        {(Object.keys(labels) as (keyof BackupDestinationInput)[]).map((key) => (
          <Field label={labels[key]!} key={key}>
            <input
              className="field__control"
              type={key === 'endpoint' ? 'url' : 'text'}
              value={input[key]}
              required={key !== 'prefix'}
              onChange={(event) => setInput({ ...input, [key]: event.target.value })}
            />
          </Field>
        ))}
        <Button type="submit">Connect destination</Button>
      </fieldset>
    </form>
  )
}

function PolicyForm({
  current,
  busy,
  onDirtyChange,
  destinations,
  onSave,
}: {
  busy: boolean
  destinations: { id: string; name: string }[]
  onSave: (input: BackupPolicyInput, baseline?: BackupConfigurationVersion) => void
  current?: BackupPolicy
  onDirtyChange: (dirty: boolean) => void
}) {
  const { api } = useAppServices()
  const origin = useApiScope(api)
  const clusters = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['backups', 'cluster-choices'],
    load: (signal) =>
      fetchBackupClusterChoices(api.baseUrl, origin.credential, api.tenantId, signal),
  })
  const [state, setState] = useState(() => backupPolicyConfiguration(current))
  const input = state.draft
  const setInput = (draft: BackupPolicyInput) => setState({ ...state, draft })
  const policyUnavailable = Boolean(state.version && !current)
  const changedOnServer = Boolean(
    current && state.version && current.revision !== state.version.revision,
  )
  const dirty = isBackupPolicyDirty(state)
  useUnsavedChanges(dirty, 'Backup policy')
  useEffect(() => {
    onDirtyChange(dirty)
    return () => onDirtyChange(false)
  }, [dirty, onDirtyChange])
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        if (!changedOnServer && !policyUnavailable) onSave(input, state.version)
      }}
    >
      {policyUnavailable && (
        <Notice tone="error">
          This policy is no longer available. The draft has been kept for review.
        </Notice>
      )}
      {changedOnServer && current && (
        <Notice tone="info">
          This policy changed on the server. Latest schedule: {current.schedule}; recovery window:{' '}
          {current.retentionDays} days.
          <Button
            disabled={busy}
            onClick={() => setState(reapplyBackupPolicyChanges(state, current))}
          >
            Reapply my changes
          </Button>
          <Button disabled={busy} onClick={() => setState(backupPolicyConfiguration(current))}>
            Discard my changes
          </Button>
        </Notice>
      )}
      <fieldset disabled={busy} className="form-grid">
        <Field label="Policy name">
          <input
            className="field__control"
            required
            disabled={Boolean(current)}
            value={input.name}
            onChange={(event) => setInput({ ...input, name: event.target.value })}
          />
        </Field>
        {clusters.error && <Notice tone="error">{clusters.error}</Notice>}
        <Field label="PostgreSQL cluster">
          <select
            className="field__control"
            required
            disabled={Boolean(current)}
            value={input.resourceId}
            onChange={(event) => setInput({ ...input, resourceId: event.target.value })}
          >
            <option value="">Select a cluster</option>
            {clusters.resource
              ?.filter((item) => item.lifecycle === 'Active')
              .map((item) => (
                <option key={item.id} value={item.id}>
                  {item.name}
                </option>
              ))}
          </select>
        </Field>
        <Field label="Destination">
          <select
            className="field__control"
            required
            value={input.destinationId}
            onChange={(event) => setInput({ ...input, destinationId: event.target.value })}
          >
            <option value="">Select destination</option>
            {destinations.map((item) => (
              <option key={item.id} value={item.id}>
                {item.name}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Schedule (UTC)">
          <input
            className="field__control"
            required
            value={input.schedule}
            onChange={(event) => setInput({ ...input, schedule: event.target.value })}
          />
        </Field>
        <Field label="Recovery window (days)">
          <input
            className="field__control"
            type="number"
            min="1"
            required
            value={input.retentionDays}
            onChange={(event) => setInput({ ...input, retentionDays: Number(event.target.value) })}
          />
        </Field>
        <Button type="submit" disabled={changedOnServer || policyUnavailable}>
          {current ? 'Save policy' : 'Create policy'}
        </Button>
      </fieldset>
    </form>
  )
}
