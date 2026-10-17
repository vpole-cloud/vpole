import { useEffect } from 'react'
import { fetchResourceGroupResources } from '../../api'
import { useScopedQuery } from '../../data/useScopedQuery'
import { groupInterests } from '../../realtime/resourceInterests'
import { useApiScope } from '../../data/useApiScope'
import { useAppServices } from '../../shell/AppContext'
import { Button, EmptyState } from '../../ui/components'
import { Pagination } from '../../ui/InventoryControls'
import { ResourceLink } from '../../ui/ResourceLink'

export function GroupResources({
  groupId,
  pageNumber,
  pageSize,
  onPage,
  onPageSize,
  compact = false,
}: {
  groupId: string
  pageNumber: number
  pageSize: number
  onPage: (page: number, replace?: boolean) => void
  onPageSize: (size: number) => void
  compact?: boolean
}) {
  const { api, resourceTypes, navigation } = useAppServices()
  const origin = useApiScope(api)
  const query = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['resource-groups', 'resources', groupId, pageNumber, pageSize],
    enabled: Boolean(api.tenantId),
    keepPrevious: true,
    realtime: groupInterests(api.tenantId, groupId),
    resourceGroupId: groupId,
    load: (signal) =>
      fetchResourceGroupResources(
        api.baseUrl,
        groupId,
        pageNumber,
        pageSize,
        origin.credential(),
        api.tenantId,
        signal,
      ),
  })
  const inventory = {
    ...query,
    page: query.resource,
    items: query.resource?.items ?? [],
    isCurrentPage: query.isCurrent,
  }
  const lastPage = Math.max(1, Math.ceil((inventory.page?.totalCount ?? 0) / pageSize))
  useEffect(() => {
    if (inventory.status === 'success' && inventory.isCurrentPage && pageNumber > lastPage)
      onPage(lastPage, true)
  }, [inventory.status, inventory.isCurrentPage, pageNumber, lastPage, onPage])
  return (
    <div className={compact ? 'resource-tree__children' : 'group-resource-inventory'}>
      {inventory.status === 'loading' && <p role="status">Loading group resources…</p>}
      {inventory.error && (
        <EmptyState title="Unable to load group resources." error>
          {inventory.error}
          {inventory.page && <p>Showing the last loaded results.</p>}
          <Button onClick={() => void inventory.refresh()}>Retry group resources</Button>
        </EmptyState>
      )}
      {inventory.status === 'success' && inventory.items.length === 0 && (
        <EmptyState title="No resources in this group." />
      )}
      <ul className="group-resource-list">
        {inventory.items.map((item) => {
          const registration = resourceTypes.find((type) => type.id === item.resourceTypeId)
          const resource = { resourceTypeId: item.resourceTypeId, resourceId: item.resourceId }
          return (
            <li key={item.registrationId}>
              <ResourceLink
                href={navigation.resourceHref(resource)}
                onNavigate={() => navigation.openResource(resource)}
              >
                {item.displayName || item.resourceId}
              </ResourceLink>
              <small>
                {registration?.label ?? item.resourceTypeId}
                {!registration && ' · UI unavailable'}
              </small>
            </li>
          )
        })}
      </ul>
      <Pagination
        label={compact ? 'Tree resources' : 'Group resources'}
        compact={compact}
        page={inventory.page}
        pageNumber={pageNumber}
        pageSize={pageSize}
        onPage={onPage}
        onPageSize={onPageSize}
      />
    </div>
  )
}
