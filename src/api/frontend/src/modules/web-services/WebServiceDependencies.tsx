import { eligibilityKeys } from '../../data/queryKeys'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { groupInterests } from '../../realtime/resourceInterests'
import { secretInterests } from '../secrets/realtime'
import { endpointInterests, endpointCompositeInterests } from '../operations/endpointRealtime'
import { connectionInterests } from './connectionRealtime'
import { rowsFor, type ReferenceRow } from './dependencyDraft'
import { useEffect, useState } from 'react'
import {
  bindSecret,
  createResourceConnection,
  deleteResourceConnection,
  detachSecret,
  fetchAvailableDeploymentTargets,
  fetchResourceConnections,
  fetchResourceEndpoints,
  fetchResourceGroupResources,
  fetchResourceGroups,
  fetchSecrets,
  fetchSecretUsage,
  fetchSecretProjections,
  updateWebServiceDependencies,
} from '../../api'
import { useWebServiceQuery } from './useWebServiceQuery'
import { useApiScope } from '../../data/useApiScope'
import { useVisibleRefresh } from '../../hooks/useVisibleRefresh'
import { invalidateWebServiceState } from './invalidate'
import { useWebServiceMutation } from './useWebServiceMutation'
import { useAppServices } from '../../shell/AppContext'
import type {
  ConnectionEnvironmentReference,
  ResourceConnection,
  ResourceEndpointDiscovery,
  SecretEnvironmentReference,
  SecretMetadata,
  WebService,
} from '../../types'
import {
  Button,
  ConfirmationDialog,
  DataTable,
  EmptyState,
  Field,
  Notice,
  StatusBadge,
} from '../../ui/components'

const sourceType = 'pole/web-service'
const discoveryReasons: Record<string, string> = {
  SourceHasNoPlacements: 'Deploy this application before creating network access.',
  DestinationTargetMissing:
    'The destination must have a deployment on every application target. Cross-target transport is unavailable.',
  DestinationTargetAmbiguous:
    'More than one destination deployment exists on an application target.',
  SourceUnsupported: 'This resource cannot initiate connections.',
  DestinationUnsupported: 'This resource does not publish connection endpoints.',
}

export function WebServiceDependencies({
  service,
  canWrite,
  onDirtyChange,
  onChanged,
  active = true,
}: {
  service: WebService
  active?: boolean
  canWrite: boolean
  onDirtyChange: (dirty: boolean) => void
  onChanged: () => Promise<void>
}) {
  const { api, navigation } = useAppServices()
  const apiScope = useApiScope(api)
  const { credential, isCurrent } = apiScope
  const scope = `${apiScope.identity}:${service.id}`
  const targets = useWebServiceQuery(
    eligibilityKeys.targets('pole/web-service', service.isolationScopeId, false),
    (signal) =>
      fetchAvailableDeploymentTargets(
        api.baseUrl,
        service.isolationScopeId ?? undefined,
        credential(),
        api.tenantId,
        signal,
        false,
      ),
    {
      active,
      realtime: [
        ...groupInterests(api.tenantId, service.resourceGroupId),
        { topic: 'deployment-target-eligibility', tenantId: api.tenantId },
      ],
      resourceGroupId: service.resourceGroupId,
    },
  )
  const targetName = (id: string) =>
    targets.resource?.find((target) => target.id === id)?.displayName ?? 'Unavailable target'
  const groups = useWebServiceQuery(
    ['resource-groups', 'choices'],
    async (signal) => {
      const first = await fetchResourceGroups(api.baseUrl, credential(), api.tenantId, signal, {
        pageSize: 100,
      })
      const items = [...first.items]
      for (let page = 2; items.length < first.totalCount; page++) {
        const next = await fetchResourceGroups(api.baseUrl, credential(), api.tenantId, signal, {
          pageNumber: page,
          pageSize: 100,
        })
        if (!next.items.length) throw new Error('Group inventory changed. Refresh to retry.')
        items.push(...next.items)
      }
      return items
    },
    { active, realtime: groupInterests(api.tenantId) },
  )
  const connections = useWebServiceQuery(
    ['connections', 'source', service.id],
    async (signal) => {
      const first = await fetchResourceConnections(
        api.baseUrl,
        service.id,
        1,
        credential(),
        api.tenantId,
        signal,
      )
      const items = [...first.items]
      for (let page = 2; items.length < first.totalCount; page++) {
        const next = await fetchResourceConnections(
          api.baseUrl,
          service.id,
          page,
          credential(),
          api.tenantId,
          signal,
        )
        if (!next.items.length) throw new Error('Connection inventory changed. Refresh to retry.')
        items.push(...next.items)
      }
      return items
    },
    { active, realtime: connectionInterests(api.tenantId) },
  )
  const secrets = useWebServiceQuery(
    ['secrets', 'choices'],
    async (signal) => {
      const items: SecretMetadata[] = []
      let after: string | undefined
      const visited = new Set<string>()
      do {
        const page = await fetchSecrets(api.baseUrl, after, credential(), api.tenantId, signal)
        items.push(...page.items)
        after = page.nextCursor ?? undefined
        if (after && visited.has(after))
          throw new Error('Secret inventory changed. Refresh to retry.')
        if (after) visited.add(after)
      } while (after)
      return items
    },
    { active, realtime: secretInterests(api.tenantId, ['secrets', 'choices']) },
  )
  const [groupId, setGroupId] = useState(service.resourceGroupId)
  const inventory = useWebServiceQuery(
    ['resource-groups', 'inventory', groupId, 'all'],
    async (signal) => {
      const first = await fetchResourceGroupResources(
        api.baseUrl,
        groupId,
        1,
        200,
        credential(),
        api.tenantId,
        signal,
      )
      const items = [...first.items]
      for (let page = 2; items.length < first.totalCount; page++) {
        const next = await fetchResourceGroupResources(
          api.baseUrl,
          groupId,
          page,
          200,
          credential(),
          api.tenantId,
          signal,
        )
        if (!next.items.length) throw new Error('Resource inventory changed. Refresh to retry.')
        items.push(...next.items)
      }
      return items
    },
    { active, realtime: groupInterests(api.tenantId, groupId), resourceGroupId: groupId },
  )
  const [destinationId, setDestinationId] = useState('')
  const [endpointId, setEndpointId] = useState('')
  const [name, setName] = useState('')
  const destination = inventory.resource?.find((item) => item.registrationId === destinationId)
  const discovery = useWebServiceQuery(
    ['endpoints', destination?.resourceTypeId, destination?.resourceId, sourceType, service.id],
    (signal) =>
      destination
        ? fetchResourceEndpoints(
            api.baseUrl,
            destination.resourceTypeId,
            destination.resourceId,
            credential(),
            api.tenantId,
            signal,
            { resourceType: sourceType, resourceId: service.id },
          )
        : Promise.resolve(null),
    {
      active,
      enabled: Boolean(destination),
      interval: 20000,
      realtime: endpointInterests(
        api.tenantId,
        destination
          ? { resourceType: destination.resourceTypeId, resourceId: destination.resourceId }
          : undefined,
        { resourceType: sourceType, resourceId: service.id },
      ),
    },
  )
  const network = useWebServiceMutation(scope, canWrite)
  const [disconnecting, setDisconnecting] = useState<ResourceConnection | null>(null)
  const refreshConnections = connections.refresh
  const pending =
    connections.resource?.some(
      (connection) =>
        connection.status === 'Deleting' ||
        connection.targets.some((target) => !['Applied', 'Completed'].includes(target.status)),
    ) ?? false
  useVisibleRefresh(active && pending, refreshConnections, 2500, connectionInterests(api.tenantId))
  const networkDirty = Boolean(destinationId || endpointId || name)
  const [configurationDirty, setConfigurationDirty] = useState(false)
  useEffect(() => {
    onDirtyChange(networkDirty || configurationDirty)
    return () => onDirtyChange(false)
  }, [networkDirty, configurationDirty, onDirtyChange])
  const duplicate = connections.resource?.some(
    (connection) =>
      connection.destinationResourceType === destination?.resourceTypeId &&
      connection.destinationResourceId === destination?.resourceId &&
      connection.endpointId === endpointId,
  )
  const canCreate =
    inventory.status === 'success' &&
    groups.status === 'success' &&
    discovery.status === 'success' &&
    discovery.resource?.topology.status === 'Eligible' &&
    discovery.resource.endpoints.some((endpoint) => endpoint.id === endpointId) &&
    !duplicate &&
    connections.status === 'success'
  return (
    <div className="dependency-workflow">
      <section className="panel">
        <h2>Application dependencies</h2>
        <p>
          Complete network access, Secret authorization and application configuration independently.
          Reload restores saved steps. No action rolls back the other steps.
        </p>
        <div className="row-actions">
          <Button
            onClick={() =>
              navigation.openResource({ resourceTypeId: 'pole/postgresql', resourceId: 'create' })
            }
            disabled={!canWrite}
          >
            Create PostgreSQL
          </Button>
          <Button
            onClick={() =>
              navigation.openResource({ resourceTypeId: 'pole/redis', resourceId: 'create' })
            }
            disabled={!canWrite}
          >
            Create Redis
          </Button>
          <Button
            onClick={() => {
              void connections.refresh()
              void secrets.refresh()
              void inventory.refresh()
              void groups.refresh()
            }}
          >
            Refresh dependencies
          </Button>
        </div>
      </section>
      <section className="panel">
        <h2>1. Network access</h2>
        <p>
          A connection grants network permission. It does not supply credentials or configure the
          application.
        </p>
        {connections.error && (
          <Notice tone="error">
            Network state is unavailable. Last fetched connections are shown. Refresh before
            creating another declaration.
          </Notice>
        )}
        {!connections.resource?.length ? (
          <EmptyState
            title={
              connections.status === 'loading'
                ? 'Loading network declarations…'
                : connections.status === 'error'
                  ? 'Network state unavailable.'
                  : 'No network access declared.'
            }
          />
        ) : (
          connections.resource.map((connection) => (
            <article className="dependency-connection" key={connection.id}>
              <div className="panel__header">
                <h3>{connection.name}</h3>
                <StatusBadge>{connection.status}</StatusBadge>
              </div>
              <a
                href={navigation.resourceHref({
                  resourceTypeId: connection.destinationResourceType,
                  resourceId: connection.destinationResourceId,
                })}
              >
                Open destination
              </a>
              <p className="field__hint">
                Endpoint {connection.endpointId} · generation {connection.generation}
              </p>
              <DataTable
                label={`${connection.name} network targets`}
                items={connection.targets}
                itemKey={(target) => `${target.targetId}:${target.phase}:${target.host}`}
                columns={[
                  {
                    id: 'target',
                    label: 'Target',
                    render: (target) => targetName(target.targetId),
                  },
                  { id: 'phase', label: 'Operation', render: (target) => target.phase },
                  {
                    id: 'status',
                    label: 'Network policy',
                    render: (target) => (
                      <StatusBadge
                        tone={
                          target.status === 'Failed'
                            ? 'danger'
                            : target.status === 'Applied' || target.status === 'Completed'
                              ? 'success'
                              : 'info'
                        }
                      >
                        {target.status}
                      </StatusBadge>
                    ),
                  },
                  {
                    id: 'endpoint',
                    label: 'Application endpoint',
                    render: (target) => (
                      <code className="dependency-host">
                        {target.host}:{target.port}
                      </code>
                    ),
                  },
                  {
                    id: 'failure',
                    label: 'Failure',
                    render: (target) => target.failureCode ?? '—',
                  },
                ]}
              />
              {canWrite && connection.status !== 'Deleting' && (
                <Button variant="danger" onClick={() => setDisconnecting(connection)}>
                  Disconnect network access
                </Button>
              )}
            </article>
          ))
        )}
        <p className="field__hint">
          Applied confirms the network policy. Verify PostgreSQL SQL or Redis operations from your
          application to prove connectivity. Failed targets are recovered independently; refresh to
          see the latest attempt.
        </p>
        {canWrite && (
          <form
            className="database-form"
            onSubmit={(event) => {
              event.preventDefault()
              if (!destination || !canCreate || network.busy) return
              void network
                .run(
                  (signal) =>
                    createResourceConnection(
                      api.baseUrl,
                      {
                        name: name.trim(),
                        sourceResourceType: sourceType,
                        sourceResourceId: service.id,
                        destinationResourceType: destination.resourceTypeId,
                        destinationResourceId: destination.resourceId,
                        endpointId,
                      },
                      credential(),
                      api.tenantId,
                      signal,
                    ),
                  async (_value, guard) => {
                    setName('')
                    setDestinationId('')
                    setEndpointId('')
                    await connections.refreshAfterWrite()
                    guard()
                    await invalidateWebServiceState(apiScope, [['connections']])
                  },
                )
                .catch(() => {})
            }}
          >
            <h3>Declare network access</h3>
            <fieldset disabled={network.busy}>
              <Field label="Destination resource group">
                <select
                  className="field__control"
                  value={groupId}
                  onChange={(event) => {
                    setGroupId(event.target.value)
                    setDestinationId('')
                    setEndpointId('')
                  }}
                >
                  {groups.resource?.map((group) => (
                    <option key={group.id} value={group.id}>
                      {group.name}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Destination resource">
                <select
                  className="field__control"
                  required
                  value={destinationId}
                  onChange={(event) => {
                    setDestinationId(event.target.value)
                    setEndpointId('')
                  }}
                >
                  <option value="">Select a resource</option>
                  {inventory.resource
                    ?.filter((item) => item.resourceId !== service.id)
                    .map((item) => (
                      <option key={item.registrationId} value={item.registrationId}>
                        {item.displayName}
                      </option>
                    ))}
                </select>
              </Field>
              <Field label="Connection endpoint">
                <select
                  className="field__control"
                  required
                  value={endpointId}
                  onChange={(event) => setEndpointId(event.target.value)}
                >
                  <option value="">Select a published endpoint</option>
                  {discovery.resource?.endpoints.map((endpoint) => (
                    <option key={endpoint.id} value={endpoint.id}>
                      {endpoint.displayName} · {endpoint.protocol} · port {endpoint.port}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Connection name">
                <input
                  className="field__control"
                  required
                  maxLength={100}
                  value={name}
                  onChange={(event) => setName(event.target.value)}
                />
              </Field>
            </fieldset>
            {groups.error || inventory.error ? (
              <Notice tone="error">
                Resource choices are unavailable. Refresh dependencies to retry.
              </Notice>
            ) : null}
            {discovery.error && (
              <Notice tone="error">
                Endpoint discovery is unavailable.
                <Button onClick={() => void discovery.refresh()}>Retry discovery</Button>
              </Notice>
            )}
            {discovery.resource?.availability === 'Unsupported' && (
              <Notice>This resource does not support connection endpoints.</Notice>
            )}
            {discovery.resource?.availability === 'Supported' &&
              !discovery.resource.endpoints.length && (
                <Notice>This resource publishes no connection endpoints.</Notice>
              )}
            {discovery.resource?.topology.status === 'Ineligible' && (
              <Notice tone="error">
                {discoveryReasons[discovery.resource.topology.reasonCode ?? ''] ??
                  'The current topology is ineligible.'}
              </Notice>
            )}
            {duplicate && (
              <Notice>
                This network declaration already exists. Continue with its Secret and configuration
                steps.
              </Notice>
            )}
            {Boolean(network.error) && (
              <Notice tone="error">
                The network request failed. Refresh the persisted declarations before retrying; an
                accepted request may already exist.
              </Notice>
            )}
            <Button type="submit" variant="primary" disabled={!canCreate || network.busy}>
              Create network access
            </Button>
          </form>
        )}
      </section>
      <DependencySecretAccess
        active={active}
        service={service}
        secrets={secrets.resource ?? []}
        choicesCurrent={hasHealthyCurrentQueryData(secrets)}
        unavailable={Boolean(secrets.error)}
        canWrite={canWrite}
        targetName={targetName}
      />
      <DependencyConfiguration
        key={service.id}
        active={active}
        service={service}
        connections={connections.resource ?? []}
        secrets={secrets.resource ?? []}
        canWrite={canWrite}
        onDirtyChange={setConfigurationDirty}
        onChanged={onChanged}
      />
      {disconnecting && (
        <ConfirmationDialog
          title="Disconnect network access?"
          confirmLabel="Disconnect network"
          busy={network.busy}
          onCancel={() => setDisconnecting(null)}
          onConfirm={() => {
            void network
              .run(
                (signal) =>
                  deleteResourceConnection(
                    api.baseUrl,
                    disconnecting.id,
                    credential(),
                    api.tenantId,
                    signal,
                  ),
                async (_value, guard) => {
                  setDisconnecting(null)
                  await connections.refreshAfterWrite()
                  guard()
                  await invalidateWebServiceState(apiScope, [['connections']])
                },
              )
              .catch(() => {
                if (isCurrent()) setDisconnecting(null)
              })
          }}
        >
          This deletes the connection declaration and requests policy cleanup on its targets. The
          database, Secret bindings and saved application configuration remain. Connection
          references become unavailable; update application configuration separately.
        </ConfirmationDialog>
      )}
    </div>
  )
}

function DependencySecretAccess({
  active,
  service,
  secrets,
  choicesCurrent,
  unavailable,
  canWrite,
  targetName,
}: {
  active: boolean
  service: WebService
  secrets: SecretMetadata[]
  choicesCurrent: boolean
  unavailable: boolean
  canWrite: boolean
  targetName: (id: string) => string
}) {
  const { api, navigation } = useAppServices()
  const [selected, setSelected] = useState('')
  const [detaching, setDetaching] = useState<{
    scope: string
    secret: SecretMetadata
    sourceId: string
    sourceRevision: number
    bindingId: string
  } | null>(null)
  const apiScope = useApiScope(api)
  const { credential, isCurrent } = apiScope
  const secret = secrets.find((item) => item.id === selected)
  const scope = `${apiScope.identity}:${service.id}:${selected}`
  const savedBindings = useWebServiceQuery(
    [
      'secrets',
      'consumer-bindings',
      service.id,
      secrets.map((item) => `${item.id}:${item.revision}`).join(','),
    ],
    async (signal) => {
      const found = await Promise.all(
        secrets
          .filter((item) => item.resourceGroupId === service.resourceGroupId)
          .map(async (item) => {
            const usage = await fetchSecretUsage(
              api.baseUrl,
              item.id,
              credential(),
              api.tenantId,
              signal,
            )
            return usage.some(
              (entry) =>
                entry.binding.consumerType === sourceType &&
                entry.binding.consumerId === service.id,
            )
              ? item
              : null
          }),
      )
      return found.filter((item): item is SecretMetadata => item !== null)
    },
    {
      enabled: Boolean(secrets.length || choicesCurrent),
      active,
      realtime: secretInterests(api.tenantId, ['secrets', 'consumer-bindings']),
    },
  )
  const usage = useWebServiceQuery(
    ['secrets', 'usage', selected],
    (signal) => fetchSecretUsage(api.baseUrl, secret!.id, credential(), api.tenantId, signal),
    {
      enabled: Boolean(secret),
      active,
      realtime: secretInterests(api.tenantId, ['secrets', 'usage', selected]),
    },
  )
  const projections = useWebServiceQuery(
    ['secrets', 'projections', selected],
    (signal) => fetchSecretProjections(api.baseUrl, secret!.id, credential(), api.tenantId, signal),
    {
      enabled: Boolean(secret),
      active,
      realtime: secretInterests(api.tenantId, ['secrets', 'projections', selected]),
    },
  )
  const binding = usage.resource?.find(
    (item) => item.binding.consumerType === sourceType && item.binding.consumerId === service.id,
  )
  const live = service.deployments.filter(
    (item) => item.desiredOperation !== 'Delete' && item.status !== 'Deleted',
  )
  const eligible = Boolean(
    secret &&
    secret.resourceGroupId === service.resourceGroupId &&
    secret.isolationScopeId === service.isolationScopeId &&
    live.length &&
    live.every((item) => item.isolationScopeId === secret.isolationScopeId),
  )
  const covered = Boolean(
    binding &&
    live.every(
      (item) =>
        binding.binding.allDeployments || binding.binding.deploymentIds.includes(item.deploymentId),
    ),
  )
  const mutation = useWebServiceMutation(scope, canWrite)
  const metadataCurrent = choicesCurrent && hasHealthyCurrentQueryData(usage)
  const canAuthorize = canWrite && metadataCurrent && eligible && !binding
  const canDetach =
    canWrite &&
    metadataCurrent &&
    Boolean(secret && binding && secret.managedBy?.bindingId !== binding.binding.id)
  const eligibleDetach =
    canDetach &&
    detaching?.scope === scope &&
    detaching.sourceId === service.id &&
    detaching.sourceRevision === service.activeRevision &&
    detaching.secret.id === secret?.id &&
    detaching.secret.revision === secret?.revision &&
    detaching.secret.resourceGroupId === secret?.resourceGroupId &&
    detaching.secret.isolationScopeId === secret?.isolationScopeId &&
    detaching.bindingId === binding?.binding.id
  const selectedProjections =
    projections.resource?.filter((item) => item.bindingId === binding?.binding.id) ?? []
  const pending = Boolean(
    binding &&
    (!selectedProjections.length ||
      selectedProjections.some(
        (item) => item.health !== 'Ready' || item.appliedVersion !== secret?.currentVersion,
      )),
  )
  const refresh = projections.refresh
  useVisibleRefresh(
    active && pending,
    refresh,
    2500,
    secretInterests(api.tenantId, ['secrets', 'projections', selected]),
  )
  return (
    <section className="panel">
      <h2>2. Secret access</h2>
      <p>
        Choose an existing Secret if the application needs credentials. Redis currently requires no
        authentication Secret. A binding authorizes projection; it does not set environment
        variables.
      </p>
      {!!savedBindings.resource?.length && (
        <div className="row-actions">
          <span>Saved application bindings:</span>
          {savedBindings.resource.map((item) => (
            <Button key={item.id} onClick={() => setSelected(item.id)}>
              {item.name}
            </Button>
          ))}
        </div>
      )}
      {savedBindings.error && (
        <Notice tone="error">
          Saved Secret bindings could not be reconstructed.
          <Button onClick={() => void savedBindings.refresh()}>Retry saved bindings</Button>
        </Notice>
      )}
      <Field label="Dependency Secret">
        <select
          className="field__control"
          value={selected}
          onChange={(event) => setSelected(event.target.value)}
        >
          <option value="">Select a Secret or leave this step optional</option>
          {secrets.map((item) => (
            <option key={item.id} value={item.id}>
              {item.name}
              {item.resourceGroupId !== service.resourceGroupId ||
              item.isolationScopeId !== service.isolationScopeId
                ? ' · unavailable in this application scope'
                : ''}
            </option>
          ))}
        </select>
      </Field>
      {unavailable && (
        <Notice tone="error">
          Secret inventory is unavailable. Refresh dependencies to retry.
        </Notice>
      )}
      {secret && (
        <>
          <a
            href={navigation.resourceHref({
              resourceTypeId: 'pole/secret',
              resourceId: secret.id,
              section: 'bindings',
            })}
          >
            Open Secret bindings
          </a>
          <p>
            Current version {secret.currentVersion} · available keys: {secret.keyNames.join(', ')}
          </p>
          {!eligible && (
            <Notice tone="error">
              Secret bindings require the same resource group and isolation scope on every
              application deployment. Network permission uses different rules. Select an eligible
              Secret.
            </Notice>
          )}
          {binding ? (
            <Notice tone={covered ? 'info' : 'error'}>
              {covered
                ? 'This application has a saved binding for every current deployment.'
                : 'The saved binding does not cover every application deployment. Review its deployment selection in Secret bindings.'}
            </Notice>
          ) : (
            <Notice>No Secret binding exists for this application.</Notice>
          )}
          {canWrite && !binding && eligible && (
            <Button
              disabled={mutation.busy || !canAuthorize}
              onClick={() => {
                if (!canAuthorize || mutation.busy) return
                void mutation
                  .run(
                    (signal) =>
                      bindSecret(
                        api.baseUrl,
                        secret.id,
                        {
                          consumerType: sourceType,
                          consumerId: service.id,
                          allDeployments: true,
                          deploymentIds: [],
                          keyMappings: {},
                        },
                        credential(),
                        api.tenantId,
                        signal,
                      ),
                    async (_value, guard) => {
                      await usage.refreshAfterWrite()
                      guard()
                      await projections.refreshAfterWrite()
                      guard()
                      await savedBindings.refreshAfterWrite()
                      guard()
                      await invalidateWebServiceState(apiScope, [['secrets']])
                    },
                  )
                  .catch(() => {})
              }}
            >
              Authorize Secret for all application deployments
            </Button>
          )}
          {!!selectedProjections.length && (
            <DataTable
              label="Dependency Secret projections"
              items={selectedProjections}
              itemKey={(item) => item.resourceDeploymentId}
              columns={[
                {
                  id: 'target',
                  label: 'Target',
                  render: (item) => targetName(item.deploymentTargetId),
                },
                {
                  id: 'state',
                  label: 'Secret projection',
                  render: (item) => (
                    <StatusBadge
                      tone={
                        item.health === 'Failed'
                          ? 'danger'
                          : item.health === 'Ready' && item.appliedVersion === secret.currentVersion
                            ? 'success'
                            : 'info'
                      }
                    >
                      {item.health === 'Ready'
                        ? item.appliedVersion === secret.currentVersion
                          ? 'Projected to target'
                          : 'Waiting for current version'
                        : item.health}
                    </StatusBadge>
                  ),
                },
                {
                  id: 'version',
                  label: 'Desired / applied',
                  render: (item) => `${item.desiredVersion} / ${item.appliedVersion ?? 'Pending'}`,
                },
                { id: 'failure', label: 'Failure', render: (item) => item.failureCode ?? '—' },
              ]}
            />
          )}
          {binding && !selectedProjections.length && (
            <Notice>Waiting for authorized projections. Refresh to inspect each target.</Notice>
          )}
          <div className="row-actions">
            <Button
              onClick={() => {
                void usage.refresh()
                void projections.refresh()
              }}
            >
              Refresh Secret access
            </Button>
            {canWrite && binding && secret.managedBy?.bindingId !== binding.binding.id && (
              <Button
                variant="danger"
                disabled={!canDetach || mutation.busy}
                onClick={() => {
                  if (canDetach && !mutation.busy)
                    setDetaching({
                      scope,
                      secret,
                      sourceId: service.id,
                      sourceRevision: service.activeRevision,
                      bindingId: binding.binding.id,
                    })
                }}
              >
                Detach application binding
              </Button>
            )}
          </div>
        </>
      )}
      {usage.error || projections.error || mutation.error ? (
        <Notice tone="error">
          Secret access is unavailable or the request failed. Refresh its saved binding and
          projections before retrying.
        </Notice>
      ) : null}
      <p className="field__hint">
        Projected confirms native Secret delivery. Saving selected references below creates an
        application release. Rotation alone does not refresh environment values in an already
        running process.
      </p>
      {detaching && canWrite && (
        <ConfirmationDialog
          title="Detach application Secret access?"
          confirmLabel="Detach binding"
          confirmDisabled={!eligibleDetach}
          busy={mutation.busy}
          onCancel={() => setDetaching(null)}
          onConfirm={() => {
            if (!eligibleDetach || mutation.busy || !isCurrent()) return
            void mutation
              .run(
                (signal) =>
                  detachSecret(
                    api.baseUrl,
                    detaching.secret.id,
                    detaching.bindingId,
                    credential(),
                    api.tenantId,
                    signal,
                  ),
                async (_value, guard) => {
                  setDetaching(null)
                  await usage.refreshAfterWrite()
                  guard()
                  await projections.refreshAfterWrite()
                  guard()
                  await savedBindings.refreshAfterWrite()
                  guard()
                  await invalidateWebServiceState(apiScope, [['secrets']])
                },
              )
              .catch(() => {
                if (isCurrent()) setDetaching(null)
              })
          }}
        >
          <p>
            This removes only this application’s binding and projections. The shared Secret,
            database and other consumers remain. Saved Secret references become unavailable until
            configured separately.
          </p>
          {!eligibleDetach && (
            <Notice>
              The application or Secret binding changed or is unavailable. Refresh access and reopen
              this confirmation.
            </Notice>
          )}
        </ConfirmationDialog>
      )}
    </section>
  )
}

function DependencyConfiguration({
  active,
  service,
  connections,
  secrets,
  canWrite,
  onDirtyChange,
  onChanged,
}: {
  active: boolean
  service: WebService
  connections: ResourceConnection[]
  secrets: SecretMetadata[]
  canWrite: boolean
  onDirtyChange: (dirty: boolean) => void
  onChanged: () => Promise<void>
}) {
  const { api } = useAppServices()
  const apiScope = useApiScope(api)
  const { credential, isCurrent } = apiScope
  const scope = `${apiScope.identity}:${service.id}`
  const [rows, setRows] = useState(() => rowsFor(service))
  const [saved, setSaved] = useState(() => JSON.stringify(rowsFor(service)))
  const [validation, setValidation] = useState('')
  const [confirming, setConfirming] = useState(false)
  const [accepted, setAccepted] = useState(false)
  const [reviewing, setReviewing] = useState(false)
  const [draftRevision, setDraftRevision] = useState(service.activeRevision)
  const [observedRevision, setObservedRevision] = useState(service.activeRevision)
  const dirty = JSON.stringify(rows) !== saved
  const mutation = useWebServiceMutation(scope, canWrite)
  if (observedRevision !== service.activeRevision && !dirty) {
    const next = rowsFor(service)
    setRows(next)
    setSaved(JSON.stringify(next))
    setObservedRevision(service.activeRevision)
    setDraftRevision(service.activeRevision)
  }
  useEffect(() => {
    onDirtyChange(dirty)
    return () => onDirtyChange(false)
  }, [dirty, onDirtyChange])
  const destinations = connections
    .filter((item) => item.status !== 'Deleting')
    .map((item) => ({
      resourceType: item.destinationResourceType,
      resourceId: item.destinationResourceId,
    }))
  const fields = useWebServiceQuery(
    [
      'endpoints',
      'dependency-fields',
      service.id,
      connections.map((item) => `${item.id}:${item.generation}:${item.endpointId}`).join(','),
      destinations,
    ],
    async (signal) => {
      const result: Record<string, ResourceEndpointDiscovery['endpoints'][number]> = {}
      const discoveries = await Promise.allSettled(
        connections
          .filter((item) => item.status !== 'Deleting')
          .map(async (connection) => {
            const discovery = await fetchResourceEndpoints(
              api.baseUrl,
              connection.destinationResourceType,
              connection.destinationResourceId,
              credential(),
              api.tenantId,
              signal,
            )
            return {
              id: connection.id,
              endpoint: discovery.endpoints.find((item) => item.id === connection.endpointId),
            }
          }),
      )
      if (signal.aborted) throw new DOMException('Aborted', 'AbortError')
      let unavailable = 0
      for (const discovery of discoveries) {
        if (discovery.status === 'fulfilled' && discovery.value.endpoint)
          result[discovery.value.id] = discovery.value.endpoint
        else unavailable++
      }
      return { endpoints: result, unavailable }
    },
    {
      active,
      interval: 20000,
      realtime: endpointCompositeInterests(api.tenantId, destinations, true),
    },
  )
  const patch = (id: number, next: Partial<ReferenceRow>) => {
    setAccepted(false)
    setRows((current) => current.map((row) => (row.id === id ? { ...row, ...next } : row)))
  }
  const add = (kind: ReferenceRow['kind']) => {
    setAccepted(false)
    setRows((current) => [
      ...current,
      {
        id: Math.max(-1, ...current.map((row) => row.id)) + 1,
        kind,
        name: '',
        connectionId: '',
        valueKind: 'Host',
        configurationKey: '',
        secretId: '',
        key: '',
      },
    ])
  }
  const save = () => {
    const connectionEnvironmentReferences: Record<string, ConnectionEnvironmentReference> = {}
    const secretEnvironmentReferences: Record<string, SecretEnvironmentReference> = {}
    for (const row of rows) {
      if (row.kind === 'connection')
        connectionEnvironmentReferences[row.name] = {
          connectionId: row.connectionId,
          valueKind: row.valueKind,
          configurationKey: row.valueKind === 'Configuration' ? row.configurationKey : null,
        }
      else secretEnvironmentReferences[row.name] = { secretId: row.secretId, key: row.key }
    }
    void mutation
      .run(
        (signal) =>
          updateWebServiceDependencies(
            api.baseUrl,
            { ...service, activeRevision: draftRevision },
            {
              ...service.spec.config,
              connectionEnvironmentReferences,
              secretEnvironmentReferences,
            },
            credential(),
            api.tenantId,
            signal,
          ),
        async (_value, guard) => {
          setConfirming(false)
          setSaved(JSON.stringify(rows))
          setAccepted(true)
          await onChanged()
          guard()
          await invalidateWebServiceState(apiScope)
        },
      )
      .catch(() => {
        if (isCurrent()) setConfirming(false)
      })
  }
  return (
    <section className="panel">
      <h2>3. Application configuration</h2>
      <p>
        Choose the environment names your application expects. Connection fields resolve
        independently on each target. Secret values enter the workload through key references.
      </p>
      {!rows.length && <EmptyState title="No dependency references configured." />}
      {!canWrite ? (
        <p>Tenant viewer access is read-only.</p>
      ) : (
        <form
          className="database-form"
          onSubmit={(event) => {
            event.preventDefault()
            if (
              rows.length > 64 ||
              new Set(rows.map((row) => row.name)).size !== rows.length ||
              rows.some(
                (row) =>
                  !/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(row.name) ||
                  Object.hasOwn(service.spec.config.environmentVariables, row.name) ||
                  (row.kind === 'connection' &&
                    (!connections.some(
                      (connection) =>
                        connection.id === row.connectionId && connection.status !== 'Deleting',
                    ) ||
                      (row.valueKind === 'Configuration' &&
                        !fields.resource?.endpoints[row.connectionId]?.configuration.some(
                          (field) => field.key === row.configurationKey,
                        )))) ||
                  (row.kind === 'secret' &&
                    !secrets.some(
                      (secret) => secret.id === row.secretId && secret.keyNames.includes(row.key),
                    )),
              )
            ) {
              setValidation(
                'Choose available logical references and distinct environment names that do not duplicate literal configuration.',
              )
              return
            }
            setValidation('')
            setConfirming(true)
          }}
        >
          <fieldset disabled={mutation.busy}>
            {rows.map((row) => (
              <div className="dependency-reference" key={row.id}>
                <Field label="Environment name">
                  <input
                    className="field__control"
                    required
                    maxLength={128}
                    value={row.name}
                    onChange={(event) => patch(row.id, { name: event.target.value })}
                  />
                </Field>
                {row.kind === 'connection' ? (
                  <>
                    <Field label="Saved network connection">
                      <select
                        className="field__control"
                        required
                        value={row.connectionId}
                        onChange={(event) =>
                          patch(row.id, { connectionId: event.target.value, configurationKey: '' })
                        }
                      >
                        <option value="">Select a connection</option>
                        {connections
                          .filter((item) => item.status !== 'Deleting')
                          .map((item) => (
                            <option key={item.id} value={item.id}>
                              {item.name}
                            </option>
                          ))}
                        {row.connectionId &&
                          !connections.some((item) => item.id === row.connectionId) && (
                            <option value={row.connectionId}>Unavailable saved connection</option>
                          )}
                      </select>
                    </Field>
                    <Field label="Connection field">
                      <select
                        className="field__control"
                        value={row.valueKind}
                        onChange={(event) =>
                          patch(row.id, {
                            valueKind: event.target.value as ReferenceRow['valueKind'],
                            configurationKey: '',
                          })
                        }
                      >
                        <option value="Host">Host for this deployment target</option>
                        <option value="Port">Service port</option>
                        <option value="Configuration">Provider configuration field</option>
                      </select>
                    </Field>
                    {row.valueKind === 'Configuration' && (
                      <Field label="Provider configuration field">
                        <select
                          className="field__control"
                          required
                          value={row.configurationKey}
                          onChange={(event) =>
                            patch(row.id, { configurationKey: event.target.value })
                          }
                        >
                          <option value="">Select a field</option>
                          {fields.resource?.endpoints[row.connectionId]?.configuration.map(
                            (field) => (
                              <option key={field.key} value={field.key}>
                                {field.displayName} · {field.value}
                              </option>
                            ),
                          )}
                        </select>
                      </Field>
                    )}
                  </>
                ) : (
                  <>
                    <Field label="Saved Secret">
                      <select
                        className="field__control"
                        required
                        value={row.secretId}
                        onChange={(event) =>
                          patch(row.id, { secretId: event.target.value, key: '' })
                        }
                      >
                        <option value="">Select a Secret</option>
                        {secrets
                          .filter(
                            (item) =>
                              item.resourceGroupId === service.resourceGroupId &&
                              item.isolationScopeId === service.isolationScopeId,
                          )
                          .map((item) => (
                            <option key={item.id} value={item.id}>
                              {item.name}
                            </option>
                          ))}
                        {row.secretId && !secrets.some((item) => item.id === row.secretId) && (
                          <option value={row.secretId}>Unavailable saved Secret</option>
                        )}
                      </select>
                    </Field>
                    <Field label="Source Secret key">
                      <select
                        className="field__control"
                        required
                        value={row.key}
                        onChange={(event) => patch(row.id, { key: event.target.value })}
                      >
                        <option value="">Select an authorized key</option>
                        {secrets
                          .find((item) => item.id === row.secretId)
                          ?.keyNames.map((key) => (
                            <option key={key}>{key}</option>
                          ))}
                      </select>
                    </Field>
                  </>
                )}
                <Button
                  variant="danger"
                  onClick={() => setRows(rows.filter((item) => item.id !== row.id))}
                >
                  Remove reference
                </Button>
              </div>
            ))}
          </fieldset>
          <div className="row-actions">
            <Button disabled={mutation.busy || rows.length >= 64} onClick={() => add('connection')}>
              Add connection reference
            </Button>
            <Button disabled={mutation.busy || rows.length >= 64} onClick={() => add('secret')}>
              Add Secret reference
            </Button>
            <Button type="submit" variant="primary" disabled={mutation.busy || !dirty}>
              Apply dependency configuration
            </Button>
            <Button
              disabled={mutation.busy || !dirty}
              onClick={() => {
                const next = rowsFor(service)
                setRows(next)
                setSaved(JSON.stringify(next))
                setValidation('')
              }}
            >
              Discard reference changes
            </Button>
          </div>
        </form>
      )}
      {!canWrite &&
        rows.map((row) => (
          <p key={row.id}>
            <code>{row.name}</code> ·{' '}
            {row.kind === 'connection'
              ? `${connections.find((item) => item.id === row.connectionId)?.name ?? 'Unavailable connection'} · ${row.valueKind}`
              : `${secrets.find((item) => item.id === row.secretId)?.name ?? 'Unavailable Secret'} · key ${row.key}`}
          </p>
        ))}
      {(fields.error || Boolean(fields.resource?.unavailable)) && (
        <Notice tone="error">
          Some provider configuration fields are unavailable. Available providers remain usable.
          <Button onClick={() => void fields.refresh()}>Retry provider fields</Button>
        </Notice>
      )}
      {validation && <Notice tone="error">{validation}</Notice>}
      {Boolean(mutation.error) && (
        <Notice tone="error">
          Dependency configuration was rejected or its result is unavailable. Refresh the service
          and review binding, key and target coverage before retrying. Your draft remains.
        </Notice>
      )}
      {dirty && draftRevision !== service.activeRevision && (
        <Notice tone="error">
          The service changed while this draft was open.
          <Button onClick={() => setReviewing(true)}>Review current references</Button>
        </Notice>
      )}
      {accepted && (
        <Notice>
          Configuration was accepted as an application release. Inspect Deployments and verify
          connectivity from the application.
        </Notice>
      )}
      <p className="field__hint">
        Existing literal environment configuration is preserved. Removing a reference does not
        delete its network declaration, Secret binding or database.
      </p>
      {confirming && (
        <ConfirmationDialog
          title="Apply dependency configuration?"
          confirmLabel="Apply configuration"
          busy={mutation.busy}
          onCancel={() => setConfirming(false)}
          onConfirm={save}
        >
          This creates a Web service release and applies the chosen references to every desired
          deployment. Current connection ownership, Secret keys, scope and coverage are checked
          again. Network and Secret declarations remain independent.
        </ConfirmationDialog>
      )}
      {reviewing && (
        <ConfirmationDialog
          title="Review the current service revision"
          confirmLabel="Keep draft for this revision"
          onCancel={() => setReviewing(false)}
          onConfirm={() => {
            setDraftRevision(service.activeRevision)
            setObservedRevision(service.activeRevision)
            setReviewing(false)
            mutation.clearError()
          }}
        >
          <p>
            Current revision {service.activeRevision} has these saved reference names:{' '}
            {rowsFor(service)
              .map((row) => row.name)
              .join(', ') || 'None'}
            .
          </p>
          <p>
            Your draft will replace those references with:{' '}
            {rows.map((row) => row.name).join(', ') || 'None'}. Current image and literal
            configuration will be preserved.
          </p>
        </ConfirmationDialog>
      )}
    </section>
  )
}
