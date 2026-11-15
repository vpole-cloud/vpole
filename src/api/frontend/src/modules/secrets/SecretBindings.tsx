import { GitOwnershipNotice } from '../../ui/GitOwnershipNotice'
import { eligibilityKeys } from '../../data/queryKeys'
import { useState } from 'react'
import {
  bindSecret,
  detachSecret,
  fetchAvailableDeploymentTargets,
  fetchResourceDeploymentSet,
  fetchResourceGroupResources,
  fetchSecretProjections,
  fetchSecretUsage,
} from '../../api'
import { useSecretQuery, useSecretScope } from './queries'
import { bindingKeyMappings, liveBindingPlacements } from './model'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import type { AvailableDeploymentTarget, ResourceRegistration, SecretMetadata } from '../../types'
import {
  Button,
  ConfirmationDialog,
  DataTable,
  EmptyState,
  Field,
  Notice,
  StatusBadge,
} from '../../ui/components'

function useGroupInventory(secret: SecretMetadata) {
  const { api } = useAppServices()
  const origin = useSecretScope()
  return useSecretQuery(
    ['resource-groups', 'complete-inventory', secret.resourceGroupId],
    async (_token, signal) => {
      const first = await fetchResourceGroupResources(
        api.baseUrl,
        secret.resourceGroupId,
        1,
        200,
        origin.credential(),
        api.tenantId,
        signal,
      )
      const items = [...first.items]
      for (let page = 2; items.length < first.totalCount; page++) {
        const next = await fetchResourceGroupResources(
          api.baseUrl,
          secret.resourceGroupId,
          page,
          200,
          origin.credential(),
          api.tenantId,
          signal,
        )
        if (!next.items.length) break
        items.push(...next.items)
      }
      return items
    },
  )
}

export function SecretBindings({
  secret,
  canWrite,
  onChanged,
}: {
  secret: SecretMetadata
  canWrite: boolean
  onChanged: () => Promise<void>
}) {
  const { api, navigation, resourceTypes } = useAppServices()
  const origin = useSecretScope()
  const scope = `${origin.writeIdentity}:${secret.id}`
  const usage = useSecretQuery(['secrets', 'usage', secret.id], (token, signal) =>
    fetchSecretUsage(api.baseUrl, secret.id, token, api.tenantId, signal),
  )
  const inventory = useGroupInventory(secret)
  const mutation = useScopedMutation(scope)
  const [detaching, setDetaching] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const allowsSharing = secret.managedBy?.sharingPolicy !== 'OwnerOnly'
  const resourceName = (type: string, id: string) =>
    inventory.resource?.find((item) => item.resourceTypeId === type && item.resourceId === id)
      ?.displayName ?? 'Unavailable resource'
  const changed = async () => {
    await usage.refreshAfterWrite()
    if (origin.isCurrent()) await onChanged()
  }
  return (
    <section className="panel">
      <header className="ui-page-header">
        <div>
          <h2>Resource bindings</h2>
          <p>
            Authorize projection to a resource’s deployments in this isolation scope. Workload
            environment configuration and network access are separate.
          </p>
        </div>
        {canWrite && allowsSharing && (
          <Button variant="primary" onClick={() => setAdding(true)}>
            Add binding
          </Button>
        )}
      </header>
      {!allowsSharing && (
        <Notice>This credential is reserved for its owning resource and cannot be shared.</Notice>
      )}
      {usage.error && <Notice tone="error">{usage.error} Last fetched usage is shown.</Notice>}
      {!usage.resource?.length ? (
        <EmptyState
          title={
            usage.status === 'loading'
              ? 'Loading bindings…'
              : usage.status === 'error'
                ? 'Bindings unavailable.'
                : 'No resource bindings.'
          }
        />
      ) : (
        <DataTable
          label="Secret bindings"
          items={usage.resource}
          itemKey={(item) => item.binding.id}
          columns={[
            {
              id: 'consumer',
              label: 'Resource',
              render: ({ binding }) => (
                <>
                  <a
                    href={navigation.resourceHref({
                      resourceTypeId: binding.consumerType,
                      resourceId: binding.consumerId,
                    })}
                  >
                    {resourceName(binding.consumerType, binding.consumerId)}
                  </a>
                  <p className="field__hint">
                    {resourceTypes.find((type) => type.id === binding.consumerType)?.label ??
                      binding.consumerType}
                  </p>
                </>
              ),
            },
            {
              id: 'deployments',
              label: 'Deployment selection',
              render: (item) =>
                item.binding.usage === 'Bootstrap'
                  ? 'Credential bootstrap only'
                  : item.binding.usage === 'BackupStorage'
                    ? 'Backup storage and catalog access'
                    : `${item.binding.allDeployments ? 'All current and future eligible deployments' : 'Selected deployments'} · ${item.affectedDeploymentIds.length} current`,
            },
            {
              id: 'keys',
              label: 'Projected keys',
              render: ({ binding }) =>
                Object.keys(binding.keyMappings).length
                  ? Object.entries(binding.keyMappings)
                      .map(([key, alias]) => `${key} → ${alias}`)
                      .join(', ')
                  : 'All current keys, same names',
            },
            {
              id: 'actions',
              label: 'Actions',
              render: ({ binding }) =>
                binding.gitOps ? (
                  <GitOwnershipNotice status={binding.gitOps} />
                ) : binding.id === secret.managedBy?.bindingId ? (
                  <StatusBadge>Protected owner binding</StatusBadge>
                ) : (
                  canWrite &&
                  !binding.gitOps && (
                    <Button variant="danger" onClick={() => setDetaching(binding.id)}>
                      Detach binding
                    </Button>
                  )
                ),
            },
          ]}
        />
      )}
      <Button
        onClick={() => {
          void usage.refresh()
          void inventory.refresh()
        }}
      >
        Refresh bindings
      </Button>
      {adding && canWrite && allowsSharing && (
        <BindingForm
          secret={secret}
          resources={inventory.resource ?? []}
          resourcesCurrent={inventory.status === 'success'}
          existing={
            usage.resource?.map(
              (item) => `${item.binding.consumerType}:${item.binding.consumerId}`,
            ) ?? []
          }
          onCancel={() => setAdding(false)}
          onSaved={async () => {
            setAdding(false)
            await changed()
          }}
        />
      )}
      {inventory.error && (
        <Notice tone="error">Resource choices are unavailable. Refresh bindings to retry.</Notice>
      )}
      {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
      {detaching && canWrite && (
        <ConfirmationDialog
          title="Detach this binding?"
          confirmLabel="Detach binding"
          busy={mutation.busy}
          onCancel={() => setDetaching(null)}
          onConfirm={() => {
            void mutation
              .run(
                (signal) =>
                  detachSecret(
                    api.baseUrl,
                    secret.id,
                    detaching,
                    origin.credential(),
                    api.tenantId,
                    signal,
                  ),
                async () => {
                  setDetaching(null)
                  await changed()
                },
              )
              .catch(() => {})
          }}
        >
          The native projection will be removed from the affected deployments. This does not delete
          the logical Secret or the consuming resource.
        </ConfirmationDialog>
      )}
    </section>
  )
}

function BindingForm({
  secret,
  resources,
  resourcesCurrent,
  existing,
  onCancel,
  onSaved,
}: {
  secret: SecretMetadata
  resources: ResourceRegistration[]
  resourcesCurrent: boolean
  existing: string[]
  onCancel: () => void
  onSaved: () => Promise<void>
}) {
  const { api, resourceTypes } = useAppServices()
  const origin = useSecretScope()
  const scope = `${origin.writeIdentity}:${secret.id}`
  const [selected, setSelected] = useState('')
  const [all, setAll] = useState(true)
  const [deploymentIds, setDeploymentIds] = useState<string[]>([])
  const [mapping, setMapping] = useState(false)
  const [aliases, setAliases] = useState<Record<string, string>>({})
  const [validation, setValidation] = useState('')
  const choices = resources.filter(
    (item) => !existing.includes(`${item.resourceTypeId}:${item.resourceId}`),
  )
  const consumer = choices.find((item) => item.registrationId === selected)
  const placements = useSecretQuery(
    ['secrets', 'binding-placements', secret.id, consumer?.resourceTypeId, consumer?.resourceId],
    (token, signal) =>
      consumer
        ? fetchResourceDeploymentSet(
            api.baseUrl,
            consumer.resourceTypeId,
            consumer.resourceId,
            token,
            api.tenantId,
            signal,
          )
        : Promise.resolve([]),
    { enabled: Boolean(consumer) },
  )
  const targets = useSecretQuery(
    eligibilityKeys.targets(consumer?.resourceTypeId, secret.isolationScopeId, false),
    (token, signal) =>
      consumer
        ? fetchAvailableDeploymentTargets(
            api.baseUrl,
            secret.isolationScopeId,
            token,
            api.tenantId,
            signal,
            false,
            consumer.resourceTypeId,
          )
        : Promise.resolve([]),
    { enabled: Boolean(consumer), resourceGroupId: secret.resourceGroupId },
  )
  const { live, eligible: matchesScope } = liveBindingPlacements(
    placements.resource ?? [],
    secret.isolationScopeId,
  )
  const eligible = resourcesCurrent && placements.status === 'success' && matchesScope
  const mutation = useScopedMutation(scope)
  return (
    <form
      className="database-form panel"
      onSubmit={(event) => {
        event.preventDefault()
        if (mutation.busy || secret.managedBy?.sharingPolicy === 'OwnerOnly') return
        const keyMappings = bindingKeyMappings(aliases, mapping)
        if (
          !consumer ||
          !eligible ||
          (!all &&
            (!deploymentIds.length ||
              deploymentIds.some((id) => !live.some((item) => item.deploymentId === id))))
        ) {
          setValidation(
            'Choose a resource with live deployments in this scope and select the required deployments.',
          )
          return
        }
        if (
          mapping &&
          (!Object.keys(keyMappings).length ||
            Object.values(keyMappings).some((alias) => !/^[A-Za-z0-9_.-]{1,128}$/.test(alias)) ||
            new Set(Object.values(keyMappings)).size !== Object.keys(keyMappings).length)
        ) {
          setValidation(
            'Choose at least one source key and unique destination key names using letters, digits, underscores, periods or hyphens.',
          )
          return
        }
        setValidation('')
        void mutation
          .run(
            (signal) =>
              bindSecret(
                api.baseUrl,
                secret.id,
                {
                  consumerType: consumer.resourceTypeId,
                  consumerId: consumer.resourceId,
                  allDeployments: all,
                  deploymentIds: all ? [] : deploymentIds,
                  keyMappings,
                },
                origin.credential(),
                api.tenantId,
                signal,
              ),
            onSaved,
          )
          .catch(() => {})
      }}
    >
      <h3>Add resource binding</h3>
      <fieldset disabled={mutation.busy}>
        <Field label="Consuming resource">
          <select
            className="field__control"
            required
            value={selected}
            onChange={(event) => {
              setSelected(event.target.value)
              setDeploymentIds([])
            }}
          >
            <option value="">Select a resource</option>
            {choices.map((item) => (
              <option key={item.registrationId} value={item.registrationId}>
                {item.displayName} ·{' '}
                {resourceTypes.find((type) => type.id === item.resourceTypeId)?.label ??
                  item.resourceTypeId}
              </option>
            ))}
          </select>
        </Field>
        {consumer && !eligible && (
          <Notice>
            {placements.status === 'loading'
              ? 'Checking live deployments…'
              : 'This resource has no eligible deployments, or its deployments cross this Secret’s isolation scope.'}
          </Notice>
        )}
        <Field label="Deployment selection">
          <select
            className="field__control"
            value={all ? 'all' : 'selected'}
            onChange={(event) => setAll(event.target.value === 'all')}
          >
            <option value="all">All current and future eligible deployments</option>
            <option value="selected">Select current deployments</option>
          </select>
        </Field>
        {!all && eligible && (
          <fieldset>
            <legend>Current deployments</legend>
            {live.map((item) => (
              <label className="checkbox-row" key={item.deploymentId}>
                <input
                  type="checkbox"
                  checked={deploymentIds.includes(item.deploymentId)}
                  onChange={(event) =>
                    setDeploymentIds(
                      event.target.checked
                        ? [...deploymentIds, item.deploymentId]
                        : deploymentIds.filter((id) => id !== item.deploymentId),
                    )
                  }
                />
                {targets.resource?.find((target) => target.id === item.deploymentTargetId)
                  ?.displayName ?? 'Unavailable target'}{' '}
                · {item.status} · deployment {item.deploymentId.slice(0, 8)}
              </label>
            ))}
          </fieldset>
        )}
        <Field label="Key projection">
          <select
            className="field__control"
            value={mapping ? 'mapped' : 'all'}
            onChange={(event) => setMapping(event.target.value === 'mapped')}
          >
            <option value="all">All current keys, same names</option>
            <option value="mapped">Select and rename keys</option>
          </select>
        </Field>
        {mapping &&
          secret.keyNames.map((key) => (
            <Field
              key={key}
              label={`Destination for ${key}`}
              hint="Leave blank to exclude this source key."
            >
              <input
                className="field__control"
                maxLength={128}
                value={aliases[key] ?? ''}
                onChange={(event) => setAliases({ ...aliases, [key]: event.target.value })}
              />
            </Field>
          ))}
      </fieldset>
      {validation && <Notice tone="error">{validation}</Notice>}
      {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
      {placements.error && (
        <Notice tone="error">
          Deployment choices are unavailable.
          <Button onClick={() => void placements.refresh()}>Refresh deployments</Button>
        </Notice>
      )}
      <div className="row-actions">
        <Button type="submit" variant="primary" disabled={!eligible || mutation.busy}>
          Create binding
        </Button>
        <Button disabled={mutation.busy} onClick={onCancel}>
          Cancel binding
        </Button>
        <Button
          onClick={() => {
            setDeploymentIds([])
            void placements.refresh()
            void targets.refresh()
          }}
        >
          Refresh choices
        </Button>
      </div>
    </form>
  )
}

export function SecretProjections({ secret }: { secret: SecretMetadata }) {
  const { api } = useAppServices()
  const origin = useSecretScope()
  const projections = useSecretQuery(
    ['secrets', 'projections', secret.id, secret.currentVersion],
    (token, signal) => fetchSecretProjections(api.baseUrl, secret.id, token, api.tenantId, signal),
    { enabled: true, interval: 2500 },
  )
  const usage = useSecretQuery(['secrets', 'usage', secret.id], (token, signal) =>
    fetchSecretUsage(api.baseUrl, secret.id, token, api.tenantId, signal),
  )
  const targets = useSecretQuery(
    [
      'secrets',
      'projection-targets',
      secret.id,
      secret.isolationScopeId,
      usage.resource?.map((item) => item.binding.consumerType),
    ],
    async (_token, signal) => {
      const result: AvailableDeploymentTarget[] = []
      for (const type of new Set(usage.resource?.map((item) => item.binding.consumerType) ?? []))
        result.push(
          ...(await fetchAvailableDeploymentTargets(
            api.baseUrl,
            secret.isolationScopeId,
            origin.credential(),
            api.tenantId,
            signal,
            false,
            type,
          )),
        )
      return result
    },
    { resourceGroupId: secret.resourceGroupId },
  )
  const pending =
    Boolean(usage.resource?.length && !projections.resource?.length) ||
    projections.resource?.some(
      (item) =>
        item.health === 'Pending' ||
        item.health === 'Failed' ||
        (item.health === 'Ready' &&
          (item.desiredVersion !== secret.currentVersion ||
            item.appliedVersion !== secret.currentVersion)),
    )
  return (
    <section className="panel">
      <header className="ui-page-header">
        <div>
          <h2>Deployment projections</h2>
          <p>
            Projected means Kubernetes accepted the Secret on that target. Application consumption
            and restart are managed separately.
          </p>
        </div>
        <Button
          onClick={() => {
            void projections.refresh()
            void usage.refresh()
            void targets.refresh()
          }}
        >
          Refresh projections
        </Button>
      </header>
      {projections.error && (
        <Notice tone="error">{projections.error} Last fetched projection results are shown.</Notice>
      )}
      {!projections.resource?.length ? (
        <EmptyState
          title={
            projections.status === 'loading'
              ? 'Loading projections…'
              : projections.status === 'error'
                ? 'Projection state unavailable.'
                : 'No deployment projections yet.'
          }
        />
      ) : (
        <DataTable
          label="Secret projections"
          items={projections.resource}
          itemKey={(item) => `${item.bindingId}:${item.resourceDeploymentId}`}
          columns={[
            {
              id: 'target',
              label: 'Deployment target',
              render: (item) => (
                <>
                  {targets.resource?.find((target) => target.id === item.deploymentTargetId)
                    ?.displayName ?? 'Unavailable target'}
                  <p className="field__hint">Deployment {item.resourceDeploymentId.slice(0, 8)}</p>
                </>
              ),
            },
            {
              id: 'status',
              label: 'Projection',
              render: (item) => (
                <StatusBadge
                  tone={
                    item.health === 'Failed'
                      ? 'danger'
                      : item.health === 'Ready' &&
                          item.desiredVersion === secret.currentVersion &&
                          item.appliedVersion === secret.currentVersion
                        ? 'success'
                        : 'neutral'
                  }
                >
                  {item.health === 'Ready'
                    ? item.desiredVersion === secret.currentVersion &&
                      item.appliedVersion === secret.currentVersion
                      ? 'Projected to target'
                      : 'Waiting for current version'
                    : item.health}
                </StatusBadge>
              ),
            },
            {
              id: 'versions',
              label: 'Desired / applied version',
              render: (item) =>
                `${item.desiredVersion} / ${item.appliedVersion ?? 'Not applied'} · current ${secret.currentVersion}`,
            },
            {
              id: 'attempt',
              label: 'Last attempt',
              render: (item) =>
                item.lastAttemptAtUtc
                  ? new Date(item.lastAttemptAtUtc).toLocaleString()
                  : 'Not attempted',
            },
            { id: 'failure', label: 'Failure', render: (item) => item.failureCode ?? '—' },
          ]}
        />
      )}
      {pending && (
        <Notice>
          Pending and failed projections are rechecked automatically. Recovery retries each target
          independently.
        </Notice>
      )}
    </section>
  )
}
