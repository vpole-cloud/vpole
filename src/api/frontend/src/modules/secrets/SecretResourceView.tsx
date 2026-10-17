import { eligibilityKeys } from '../../data/queryKeys'
import { useEffect, useState } from 'react'
import {
  deleteSecret,
  fetchIsolationScopes,
  fetchResourceGroup,
  fetchSecret,
  rotateSecret,
  SecretApiError,
} from '../../api'
import { useSecretQuery, useSecretScope } from './queries'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import type { SecretMetadata } from '../../types'
import { Button, ConfirmationDialog, EmptyState, Notice, StatusBadge } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { Tabs, TabPanel } from '../../ui/Tabs'
import { SecretBindings, SecretProjections } from './SecretBindings'
import { SecretValues } from './SecretValues'
import { useClearOnLeave, useWriteOnlyValues, validateValues } from './useWriteOnlyValues'
import type { SecretSection } from './workspaceState'

export function SecretResourceView({
  id,
  section,
  active,
  onSection,
  actions,
}: {
  id: string
  section: SecretSection
  active: boolean
  onSection: (section: SecretSection) => void
  actions: WorkspaceActions<SecretSection>
}) {
  const { api, tenant, navigation, resourceTypes } = useAppServices()
  const origin = useSecretScope()
  const scope = `${origin.writeIdentity}:${id}`
  const details = useSecretQuery(
    ['secrets', 'detail', id],
    (token, signal) => fetchSecret(api.baseUrl, id, token, api.tenantId, signal),
    { active, interval: section === 'projections' ? 5000 : false },
  )
  const secret = details.resource
  const group = useSecretQuery(
    ['resource-groups', 'detail', secret?.resourceGroupId],
    (token, signal) =>
      secret
        ? fetchResourceGroup(api.baseUrl, secret.resourceGroupId, token, api.tenantId, signal)
        : Promise.resolve(null),
    { enabled: Boolean(secret), active },
  )
  const scopes = useSecretQuery(
    eligibilityKeys.scopes(secret?.resourceGroupId),
    (token, signal) =>
      secret
        ? fetchIsolationScopes(api.baseUrl, secret.resourceGroupId, token, api.tenantId, signal)
        : Promise.resolve([]),
    { enabled: Boolean(secret), active },
  )
  const mutation = useScopedMutation(scope)
  const [deleting, setDeleting] = useState(false)
  const onLoaded = actions.loaded
  useEffect(() => {
    if (secret) onLoaded(id, secret.name)
  }, [secret, id, onLoaded])
  if (!secret)
    return (
      <EmptyState
        title={details.status === 'error' ? 'Unable to open this Secret.' : 'Loading Secret…'}
        error={details.status === 'error'}
      >
        {details.error && <p>{details.error}</p>}
        <Button onClick={() => void details.refresh()}>Retry Secret</Button>
      </EmptyState>
    )
  const canWrite = tenant.canManage && secret.lifecycle === 'Active'
  const panel = `secret-${id}`
  const changed = async () => {
    await origin.invalidate([['secrets']])
  }
  return (
    <div>
      <header className="ui-page-header">
        <div>
          <p className="ui-page-eyebrow">Secret · {group.resource?.name ?? 'Resource group'}</p>
          <h1>{secret.name}</h1>
          <p>{secret.purpose || 'Encrypted, write-only values'}</p>
        </div>
        <div className="row-actions">
          <Button onClick={() => void details.refresh()}>Refresh metadata</Button>
          {canWrite && !secret.managedBy && (
            <Button variant="danger" onClick={() => setDeleting(true)}>
              Delete Secret
            </Button>
          )}
        </div>
      </header>
      {details.error && (
        <Notice tone="error">{details.error} Last fetched metadata is shown.</Notice>
      )}
      <Tabs
        id={panel}
        label={`${secret.name} sections`}
        variant="section"
        activeId={section}
        onSelect={(value) => onSection(value as SecretSection)}
        tabs={[
          { id: 'overview', label: 'Overview' },
          { id: 'rotate', label: 'Rotate values' },
          { id: 'bindings', label: 'Bindings' },
          { id: 'projections', label: 'Projections' },
        ]}
      />
      <TabPanel id={panel} tabId="overview" activeId={section}>
        <section className="panel">
          <div className="panel__header">
            <h2 className="panel__title">Secret metadata</h2>
            <StatusBadge>{secret.lifecycle}</StatusBadge>
          </div>
          <dl className="web-service-facts">
            <div>
              <dt>Resource group</dt>
              <dd>
                <a
                  href={navigation.resourceHref({
                    resourceTypeId: 'pole/resource-group',
                    resourceId: secret.resourceGroupId,
                  })}
                >
                  {group.resource?.name ?? secret.resourceGroupId}
                </a>
              </dd>
            </div>
            <div>
              <dt>Isolation scope</dt>
              <dd>
                {scopes.resource?.find((item) => item.id === secret.isolationScopeId)?.name ??
                  secret.isolationScopeId}
              </dd>
            </div>
            <div>
              <dt>Keys</dt>
              <dd>{secret.keyNames.join(', ')}</dd>
            </div>
            <div>
              <dt>Current version</dt>
              <dd>{secret.currentVersion}</dd>
            </div>
            <div>
              <dt>Revision</dt>
              <dd>{secret.revision}</dd>
            </div>
            <div>
              <dt>Updated</dt>
              <dd>{new Date(secret.updatedAtUtc).toLocaleString()}</dd>
            </div>
          </dl>
          <p className="field__hint">
            Stored values cannot be revealed or downloaded. Name, group and isolation scope are
            fixed.
          </p>
          {secret.managedBy && (
            <Notice>
              Managed by{' '}
              <a
                href={navigation.resourceHref({
                  resourceTypeId: secret.managedBy.resourceType,
                  resourceId: secret.managedBy.resourceId,
                })}
              >
                the owning{' '}
                {resourceTypes.find((type) => type.id === secret.managedBy!.resourceType)?.label ??
                  'resource'}
              </a>
              . Change its credentials there. Additional sharing bindings can be managed here.
            </Notice>
          )}
        </section>
      </TabPanel>
      <TabPanel id={panel} tabId="rotate" activeId={section}>
        {active &&
          section === 'rotate' &&
          (canWrite && !secret.managedBy ? (
            <SecretRotation
              secret={secret}
              onDirty={actions.dirty}
              onSaved={changed}
              onRefresh={details.refresh}
            />
          ) : (
            <EmptyState
              title={
                secret.managedBy
                  ? 'Rotate credentials through the owning resource.'
                  : 'Secret rotation is read-only.'
              }
            />
          ))}
      </TabPanel>
      <TabPanel id={panel} tabId="bindings" activeId={section}>
        {active && section === 'bindings' && (
          <SecretBindings secret={secret} canWrite={canWrite} onChanged={changed} />
        )}
      </TabPanel>
      <TabPanel id={panel} tabId="projections" activeId={section}>
        {active && section === 'projections' && <SecretProjections secret={secret} />}
      </TabPanel>
      {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
      {deleting && canWrite && !secret.managedBy && (
        <ConfirmationDialog
          title={`Delete ${secret.name}?`}
          confirmLabel="Delete Secret"
          busy={mutation.busy}
          onCancel={() => setDeleting(false)}
          onConfirm={() => {
            void mutation
              .run(
                (signal) =>
                  deleteSecret(api.baseUrl, id, origin.credential(), api.tenantId, signal),
                async () => {
                  await origin.invalidate([
                    ['secrets'],
                    ['resource-groups'],
                    ['overview'],
                    ['search'],
                  ])
                  if (!origin.isCurrent()) return
                  setDeleting(false)
                  actions.closed(id)
                },
              )
              .catch(() => {})
          }}
        >
          Delete this encrypted Secret permanently. Every binding must be detached first.
        </ConfirmationDialog>
      )}
    </div>
  )
}

function SecretRotation({
  secret,
  onDirty,
  onSaved,
  onRefresh,
}: {
  secret: SecretMetadata
  onDirty: WorkspaceActions<SecretSection>['dirty']
  onSaved: () => Promise<void>
  onRefresh: () => Promise<SecretMetadata | null>
}) {
  const { api, notifications } = useAppServices()
  const origin = useSecretScope()
  const values = useWriteOnlyValues(secret.keyNames)
  const [expectedVersion, setExpectedVersion] = useState(secret.currentVersion)
  const [validation, setValidation] = useState('')
  const [confirming, setConfirming] = useState(false)
  const mutation = useScopedMutation(`${origin.writeIdentity}:${secret.id}`)
  const clear = () => {
    values.clear()
    setConfirming(false)
  }
  useClearOnLeave(clear, mutation.cancel)
  const dirty =
    values.rows.some((row) => row.confirmed) ||
    JSON.stringify(values.rows.map((row) => row.key)) !== JSON.stringify(secret.keyNames)
  useUnsavedChanges(dirty, secret.name)
  useEffect(() => {
    onDirty(secret.id, dirty)
    return () => onDirty(secret.id, false)
  }, [onDirty, secret.id, dirty])
  const conflict =
    expectedVersion !== secret.currentVersion ||
    (mutation.error instanceof SecretApiError && mutation.error.code === 'secrets.version-conflict')
  return (
    <section className="panel">
      <h2 className="panel__title">Rotate Secret values</h2>
      <p>
        Replace the entire key/value map at version {expectedVersion}. Keys omitted from this map
        are removed. Existing values cannot be prefilled.
      </p>
      {conflict && (
        <Notice tone="error">
          The current version must be reviewed before resubmitting.
          <Button
            onClick={() => {
              void onRefresh().then((latest) => {
                if (latest && origin.isCurrent()) {
                  setExpectedVersion(latest.currentVersion)
                  mutation.clearError()
                  setValidation(
                    'Review every replacement key and value against the refreshed metadata before submitting.',
                  )
                }
              })
            }}
          >
            Refresh and review current version
          </Button>
        </Notice>
      )}
      <form
        className="database-form"
        onSubmit={(event) => {
          event.preventDefault()
          const error = validateValues(values.rows)
          if (error) {
            setValidation(error)
            return
          }
          if (!mutation.busy && !conflict) {
            setValidation('')
            setConfirming(true)
          }
        }}
      >
        <fieldset disabled={mutation.busy}>
          <SecretValues rows={values.rows} onChange={values.setRows} cleared={values.cleared} />
        </fieldset>
        {validation && <Notice>{validation}</Notice>}
        {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
        <Button type="submit" variant="primary" disabled={mutation.busy || conflict}>
          Review replacement
        </Button>
      </form>
      {confirming && (
        <ConfirmationDialog
          title={`Replace all values in ${secret.name}?`}
          confirmLabel="Rotate Secret"
          busy={mutation.busy}
          onCancel={() => setConfirming(false)}
          onConfirm={() => {
            void mutation
              .run(
                (signal) =>
                  rotateSecret(
                    api.baseUrl,
                    secret.id,
                    expectedVersion,
                    Object.fromEntries(values.rows.map((row) => [row.key, row.value])),
                    origin.credential(),
                    api.tenantId,
                    signal,
                  ),
                async (updated) => {
                  clear()
                  setExpectedVersion(updated.currentVersion)
                  values.setRows(updated.keyNames.map((key) => ({ key, value: '' })))
                  await onSaved()
                  if (!origin.isCurrent()) return
                  notifications.publish(
                    'Secret rotation accepted. Check each deployment projection for convergence.',
                  )
                },
              )
              .catch(() => {
                if (origin.isCurrent()) setConfirming(false)
              })
          }}
        >
          The complete replacement contains {values.rows.length} keys. Existing bindings remain.
          Applications may need their own restart or configuration update.
        </ConfirmationDialog>
      )}
    </section>
  )
}
