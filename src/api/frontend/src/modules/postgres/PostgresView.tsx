import { useEffect } from 'react'
import { fetchPostgresClusters } from '../../api'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useApiScope } from '../../data/useApiScope'
import { resourceInterests } from '../../realtime/resourceInterests'
import { useAppServices } from '../../shell/AppContext'
import { readResourceRoute } from '../../shell/workspaceState'
import { Button, DataTable, EmptyState, Notice, StatusBadge } from '../../ui/components'
import { Pagination } from '../../ui/InventoryControls'
import { ResourceWorkspace, type WorkspaceActions } from '../../ui/ResourceWorkspace'
import { NewPostgresView } from './PostgresForm'
import { PostgresResourceView } from './PostgresResourceView'
import { postgresSections, type PostgresSection } from './workspaceState'

export function PostgresView() {
  return (
    <ResourceWorkspace
      id="postgres"
      label="databases"
      newLabel="New PostgreSQL"
      sections={postgresSections}
      defaultSection="overview"
      Catalog={PostgresCatalog}
      Create={NewPostgresView}
      Resource={PostgresResourceView}
    />
  )
}

function PostgresCatalog({
  actions,
  active,
}: {
  actions: WorkspaceActions<PostgresSection>
  active: boolean
}) {
  const { api, tenant, navigation } = useAppServices()
  const origin = useApiScope(api)
  const route = readResourceRoute(navigation.search, postgresSections, 'overview')
  const query = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['postgres', 'inventory', route.pageNumber, route.pageSize],
    enabled: Boolean(tenant.active),
    keepPrevious: true,
    active,
    realtime: resourceInterests('postgres', api.tenantId),
    load: (signal) =>
      fetchPostgresClusters(
        api.baseUrl,
        origin.credential(),
        api.tenantId,
        signal,
        route.pageNumber,
        route.pageSize,
      ),
  })
  const inventory = {
    ...query,
    page: query.resource,
    items: query.resource?.items ?? [],
    isCurrentPage: query.isCurrent,
  }
  const lastPage = Math.max(1, Math.ceil((inventory.page?.totalCount ?? 0) / route.pageSize))
  useEffect(() => {
    if (inventory.status === 'success' && inventory.isCurrentPage && route.pageNumber > lastPage) {
      const params = new URLSearchParams(navigation.search)
      params.set('page', String(lastPage))
      navigation.setSearch(params, { replace: true })
    }
  }, [inventory.status, inventory.isCurrentPage, route.pageNumber, lastPage, navigation])
  const page = (pageNumber: number, pageSize = route.pageSize) => {
    const params = new URLSearchParams(navigation.search)
    params.set('page', String(pageNumber))
    params.set('pageSize', String(pageSize))
    navigation.setSearch(params)
  }
  return (
    <section className="panel">
      <header className="ui-page-header">
        <div>
          <p className="ui-page-eyebrow">Data services</p>
          <h1>PostgreSQL databases</h1>
          <p>Provision one database cluster on one deployment target.</p>
        </div>
        <div className="row-actions">
          <Button
            disabled={inventory.status === 'loading'}
            onClick={() => void inventory.refresh()}
          >
            Refresh databases
          </Button>
          {tenant.canManage && (
            <Button variant="primary" onClick={() => actions.open('create')}>
              New PostgreSQL
            </Button>
          )}
        </div>
      </header>
      {inventory.error && (
        <Notice tone="error">{inventory.error} Previously fetched rows remain visible.</Notice>
      )}
      {!inventory.items.length ? (
        <EmptyState
          title={
            inventory.status === 'loading'
              ? 'Loading databases…'
              : inventory.status === 'error'
                ? 'Unable to load databases.'
                : 'No PostgreSQL databases yet.'
          }
        />
      ) : (
        <DataTable
          label="PostgreSQL databases"
          items={inventory.items}
          itemKey={(cluster) => cluster.id}
          rowTestId={(cluster) => `resource-row-${cluster.id}`}
          columns={[
            {
              id: 'name',
              label: 'Database',
              render: (cluster) => (
                <a
                  href={navigation.resourceHref({
                    resourceTypeId: 'pole/postgresql',
                    resourceId: cluster.id,
                  })}
                  onClick={(event) => {
                    if (!event.ctrlKey && !event.metaKey && !event.shiftKey && event.button === 0) {
                      event.preventDefault()
                      actions.loaded(cluster.id, cluster.name)
                      actions.open(cluster.id)
                    }
                  }}
                >
                  {cluster.name}
                </a>
              ),
            },
            {
              id: 'version',
              label: 'Version',
              render: (cluster) => `PostgreSQL ${cluster.spec.version}`,
            },
            {
              id: 'size',
              label: 'Capacity',
              render: (cluster) =>
                `${cluster.spec.instances} instance(s) · ${cluster.spec.storage} GiB each`,
            },
            {
              id: 'mode',
              label: 'Provisioning',
              render: (cluster) =>
                cluster.spec.deploymentMode === 'Operator' ? 'CloudNativePG' : 'Native StatefulSet',
            },
            {
              id: 'status',
              label: 'Status',
              render: (cluster) => (
                <StatusBadge tone={cluster.placement.failureCode ? 'danger' : 'neutral'}>
                  {cluster.lifecycle === 'Deleting' ? 'Deleting' : cluster.placement.status}
                </StatusBadge>
              ),
            },
          ]}
        />
      )}
      <Pagination
        label="PostgreSQL"
        page={inventory.page}
        pageNumber={route.pageNumber}
        pageSize={route.pageSize}
        onPage={page}
        onPageSize={(size) => page(1, size)}
      />
    </section>
  )
}
