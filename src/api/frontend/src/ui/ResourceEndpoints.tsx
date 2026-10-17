import { fetchResourceEndpoints } from '../modules/operations/api'
import { useScopedQuery } from '../data/useScopedQuery'
import { useApiScope } from '../data/useApiScope'
import { endpointInterests } from '../modules/operations/endpointRealtime'
import { useAppServices } from '../shell/AppContext'
import { Button, DataTable, EmptyState, Notice, StatusBadge } from './components'

export function ResourceEndpoints({
  resourceType,
  resourceId,
  source,
  active = true,
}: {
  resourceType: string
  resourceId: string
  active?: boolean
  source?: { resourceType: string; resourceId: string }
}) {
  const { api } = useAppServices()
  const { credential } = useApiScope(api)
  const discovery = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['endpoints', resourceType, resourceId, source?.resourceType, source?.resourceId],
    active,
    realtime: endpointInterests(api.tenantId, { resourceType, resourceId }, source),
    interval: 20000,
    load: (signal) =>
      fetchResourceEndpoints(
        api.baseUrl,
        resourceType,
        resourceId,
        credential(),
        api.tenantId,
        signal,
        source,
      ),
  })
  const value = discovery.resource
  const reasons: Record<string, string> = {
    SourceUnsupported: 'This resource cannot initiate connections.',
    DestinationUnsupported: 'This resource does not publish connection endpoints.',
    SourceHasNoPlacements: 'The source has no current deployment.',
    DestinationTargetMissing:
      'The destination is missing on at least one source deployment target. Cross-target transport is unavailable.',
    DestinationTargetAmbiguous: 'More than one destination deployment exists on a source target.',
  }
  return (
    <section className="panel">
      <div className="panel__header">
        <h2 className="panel__title">Connection endpoints</h2>
        <Button onClick={() => void discovery.refresh()}>Refresh endpoints</Button>
      </div>
      <p className="field__hint">
        Published endpoints contain no credentials. Network permission and application configuration
        are separate. Availability is checked again when a connection is created.
      </p>
      {discovery.error && (
        <Notice tone="error">
          Endpoint discovery is unavailable. Refresh to retry.
          {value && ' Last fetched endpoints are shown.'}
        </Notice>
      )}
      {!value ? (
        <EmptyState
          title={
            discovery.status === 'error' ? 'Unable to discover endpoints.' : 'Loading endpoints…'
          }
        />
      ) : value.availability === 'Unsupported' ? (
        <EmptyState title="This resource does not support connection endpoints." />
      ) : !value.endpoints.length ? (
        <EmptyState title="This resource publishes no connection endpoints." />
      ) : (
        <DataTable
          label="Connection endpoints"
          items={value.endpoints}
          itemKey={(item) => item.id}
          columns={[
            {
              id: 'name',
              label: 'Endpoint',
              render: (item) => (
                <>
                  {item.displayName}
                  <p className="field__hint">{item.id}</p>
                </>
              ),
            },
            {
              id: 'protocol',
              label: 'Protocol',
              render: (item) => `${item.protocol} · ${item.transport}`,
            },
            { id: 'port', label: 'Service port', render: (item) => item.port },
            {
              id: 'config',
              label: 'Configuration',
              render: (item) =>
                item.configuration
                  .map((field) => `${field.displayName}: ${field.value}`)
                  .join(', ') || 'No additional fields',
            },
          ]}
        />
      )}
      {value?.topology.status === 'Ineligible' && (
        <Notice tone="error">
          {reasons[value.topology.reasonCode ?? ''] ??
            'The current topology cannot support this connection.'}
        </Notice>
      )}
      {value?.topology.status === 'Eligible' && (
        <StatusBadge tone="success">Deployment targets match</StatusBadge>
      )}
      {value?.topology.status === 'NotEvaluated' && (
        <p className="field__hint">
          Select a source resource when configuring a dependency to check deployment targets.
        </p>
      )}
    </section>
  )
}
