// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useEffect } from 'react'
import { fetchRedisServices } from '../../api'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useApiScope } from '../../data/useApiScope'
import { resourceInterests } from '../../realtime/resourceInterests'
import { useAppServices } from '../../shell/AppContext'
import { readResourceRoute } from '../../shell/workspaceState'
import { Button, DataTable, EmptyState, Notice, StatusBadge } from '../../ui/components'
import { InventorySearch, Pagination } from '../../ui/InventoryControls'
import { ResourceWorkspace, type WorkspaceActions } from '../../ui/ResourceWorkspace'
import { NewRedisView } from './RedisForm'
import { RedisResourceView } from './RedisResourceView'
import { redisSections, type RedisSection } from './workspaceState'

export function RedisView() {
  return (
    <ResourceWorkspace
      id="redis"
      label="Redis services"
      newLabel="New Redis"
      sections={redisSections}
      defaultSection="overview"
      Catalog={RedisCatalog}
      Create={NewRedisView}
      Resource={RedisResourceView}
    />
  )
}

function RedisCatalog({
  actions,
  active,
}: {
  actions: WorkspaceActions<RedisSection>
  active: boolean
}) {
  const { api, tenant, navigation } = useAppServices()
  const origin = useApiScope(api)
  const route = readResourceRoute(navigation.search, redisSections, 'overview')
  const search = route.search
  const query = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['redis', 'inventory', route.pageNumber, route.pageSize, search],
    enabled: Boolean(tenant.active),
    keepPrevious: true,
    active,
    realtime: resourceInterests('redis', api.tenantId),
    load: (signal) =>
      fetchRedisServices(
        api.baseUrl,
        origin.credential(),
        api.tenantId,
        signal,
        route.pageNumber,
        route.pageSize,
        search,
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
          <h1>Redis services</h1>
          <p>Provision one persistent Redis instance on one deployment target.</p>
        </div>
        <div className="row-actions">
          <Button
            disabled={inventory.status === 'loading'}
            onClick={() => void inventory.refresh()}
          >
            Refresh Redis services
          </Button>
          {tenant.canManage && (
            <Button variant="primary" onClick={() => actions.open('create')}>
              New Redis
            </Button>
          )}
        </div>
      </header>
      <InventorySearch
        search={search}
        placeholder="Search Redis services"
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
              ? 'Loading Redis services…'
              : inventory.status === 'error'
                ? 'Unable to load Redis services.'
                : search
                  ? 'No Redis services match this search.'
                  : 'No Redis services yet.'
          }
        />
      ) : (
        <DataTable
          label="Redis services"
          items={inventory.items}
          itemKey={(redis) => redis.id}
          rowTestId={(redis) => `resource-row-${redis.id}`}
          columns={[
            {
              id: 'name',
              label: 'Service',
              render: (redis) => (
                <a
                  href={navigation.resourceHref({
                    resourceTypeId: 'pole/redis',
                    resourceId: redis.id,
                  })}
                  onClick={(event) => {
                    if (!event.ctrlKey && !event.metaKey && !event.shiftKey && event.button === 0) {
                      event.preventDefault()
                      actions.loaded(redis.id, redis.name)
                      actions.open(redis.id)
                    }
                  }}
                >
                  {redis.name}
                </a>
              ),
            },
            {
              id: 'storage',
              label: 'Storage',
              render: (redis) => `${redis.spec.storage.sizeGb} GiB`,
            },
            { id: 'tags', label: 'Tags', render: (redis) => redis.tags.join(', ') || 'No tags' },
            {
              id: 'status',
              label: 'Status',
              render: (redis) => (
                <StatusBadge tone={redis.placement?.failureCode ? 'danger' : 'neutral'}>
                  {redis.lifecycle === 'Deleting'
                    ? 'Deleting'
                    : (redis.placement?.status ?? 'Unavailable')}
                </StatusBadge>
              ),
            },
          ]}
        />
      )}
      <Pagination
        label="Redis"
        page={inventory.page}
        pageNumber={route.pageNumber}
        pageSize={route.pageSize}
        onPage={page}
        onPageSize={(size) => page(1, size)}
      />
    </section>
  )
}
