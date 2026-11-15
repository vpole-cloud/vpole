import { useEffect } from 'react'
import { listSeaweedFS } from '../../api'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useApiScope } from '../../data/useApiScope'
import { useAppServices } from '../../shell/AppContext'
import { readResourceRoute } from '../../shell/workspaceState'
import { Button, DataTable, EmptyState, Notice, StatusBadge } from '../../ui/components'
import { InventorySearch, Pagination } from '../../ui/InventoryControls'
import { ResourceWorkspace, type WorkspaceActions } from '../../ui/ResourceWorkspace'
import { NewSeaweedFSView } from './SeaweedFSForm'
import { SeaweedFSResourceView } from './SeaweedFSResourceView'
const seaweedFSSections = ['overview'] as const
export type SeaweedFSSection = (typeof seaweedFSSections)[number]

export function SeaweedFSView() {
  return (
    <ResourceWorkspace
      id="seaweedfs"
      label="SeaweedFS instances"
      newLabel="New SeaweedFS instance"
      sections={seaweedFSSections}
      defaultSection="overview"
      Catalog={SeaweedFSCatalog}
      Create={NewSeaweedFSView}
      Resource={SeaweedFSResourceView}
    />
  )
}

function SeaweedFSCatalog({
  actions,
  active,
}: {
  actions: WorkspaceActions<SeaweedFSSection>
  active: boolean
}) {
  const { api, tenant, navigation } = useAppServices()
  const origin = useApiScope(api)
  const route = readResourceRoute(navigation.search, seaweedFSSections, 'overview')
  const search = route.search
  const query = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['seaweedfs', 'inventory', route.pageNumber, route.pageSize, search],
    enabled: Boolean(tenant.active),
    keepPrevious: true,
    active,
    load: (signal) =>
      listSeaweedFS(
        { ...api, token: origin.credential() },
        route.pageNumber,
        route.pageSize,
        search,
        signal,
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
          <h1>SeaweedFS instances</h1>
        </div>
        <div className="row-actions">
          <Button
            disabled={inventory.status === 'loading'}
            onClick={() => void inventory.refresh()}
          >
            Refresh SeaweedFS instances
          </Button>
          {tenant.canManage && (
            <Button variant="primary" onClick={() => actions.open('create')}>
              New SeaweedFS instance
            </Button>
          )}
        </div>
      </header>
      <InventorySearch
        search={search}
        placeholder="Search SeaweedFS instances"
        onSearch={(value) => {
          const params = new URLSearchParams(navigation.search)
          params.set('search', value)
          params.set('page', '1')
          navigation.setSearch(params)
        }}
      />
      {inventory.error && (
        <Notice tone="error">{inventory.error} Previously fetched rows remain visible.</Notice>
      )}
      {!inventory.items.length ? (
        <EmptyState
          title={
            inventory.status === 'loading'
              ? 'Loading SeaweedFS instances…'
              : inventory.status === 'error'
                ? 'Unable to load SeaweedFS instances.'
                : search
                  ? 'No SeaweedFS instances match this search.'
                  : 'No SeaweedFS instances yet.'
          }
        />
      ) : (
        <DataTable
          label="SeaweedFS instances"
          items={inventory.items}
          itemKey={(instance) => instance.id}
          rowTestId={(instance) => `resource-row-${instance.id}`}
          columns={[
            {
              id: 'name',
              label: 'Service',
              render: (instance) => (
                <a
                  href={navigation.resourceHref({
                    resourceTypeId: 'pole/seaweedfs',
                    resourceId: instance.id,
                  })}
                  onClick={(event) => {
                    if (!event.ctrlKey && !event.metaKey && !event.shiftKey && event.button === 0) {
                      event.preventDefault()
                      actions.loaded(instance.id, instance.name)
                      actions.open(instance.id)
                    }
                  }}
                >
                  {instance.name}
                </a>
              ),
            },
            {
              id: 'storage',
              label: 'Storage',
              render: (instance) => `${instance.spec.storage.sizeGb} GiB`,
            },
            {
              id: 'tags',
              label: 'Tags',
              render: (instance) => instance.tags.join(', ') || 'No tags',
            },
            {
              id: 'status',
              label: 'Status',
              render: (instance) => (
                <StatusBadge tone={instance.placement?.failureCode ? 'danger' : 'neutral'}>
                  {instance.lifecycle === 'Deleting'
                    ? 'Deleting'
                    : (instance.placement?.status ?? 'Unavailable')}
                </StatusBadge>
              ),
            },
          ]}
        />
      )}
      <Pagination
        label="SeaweedFS"
        page={inventory.page}
        pageNumber={route.pageNumber}
        pageSize={route.pageSize}
        onPage={page}
        onPageSize={(size) => page(1, size)}
      />
    </section>
  )
}
