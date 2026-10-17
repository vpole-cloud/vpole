import { useState } from 'react'
import { Button, ConfirmationDialog, DataTable, EmptyState, StatusBadge } from '../ui/components'
import { InventorySearch, Pagination } from '../ui/InventoryControls'
import { ResourceLink } from '../ui/ResourceLink'
import type { LoadStatus, PaginatedResponse, ResourceGroup, WebService } from '../types'

export function WebServicesPage({
  items,
  resourceGroups,
  status,
  error,
  page,
  pageNumber,
  pageSize,
  search,
  onPage,
  onPageSize,
  onSearch,
  deletingId,
  deleteError,
  canManage,
  onRefresh,
  onCreate,
  onOpen,
  resourceHref,
  onDelete,
}: {
  items: WebService[]
  resourceGroups: ResourceGroup[]
  status: LoadStatus
  error?: string
  deletingId: string | null
  page: PaginatedResponse<WebService> | null
  pageNumber: number
  pageSize: number
  search: string
  onPage: (pageNumber: number) => void
  onPageSize: (pageSize: number) => void
  onSearch: (search: string) => void
  deleteError?: string
  canManage: boolean
  onRefresh: () => void
  onCreate: () => void
  onOpen: (id: string, edit?: boolean) => void
  resourceHref: (id: string) => string
  onDelete: (id: string) => Promise<void>
}) {
  const [deleteCandidate, setDeleteCandidate] = useState<WebService | null>(null)

  return (
    <div className="web-service-catalog">
      <header className="ui-page-header">
        <div>
          <h1>Web services</h1>
          <p>Open a service to inspect its configuration, deployments and builds.</p>
        </div>
        <div className="row-actions">
          <Button aria-label="Refresh web services" onClick={onRefresh}>
            Refresh
          </Button>
          <Button variant="primary" disabled={!canManage} onClick={onCreate}>
            New service
          </Button>
        </div>
      </header>
      <section className="panel resource-groups-list-panel">
        <div className="panel__header">
          <h2 className="panel__title">All web services</h2>
          <span className="panel__meta">
            {page ? `${page.totalCount} service${page.totalCount === 1 ? '' : 's'}` : 'Inventory'}
          </span>
        </div>
        <InventorySearch
          search={search}
          placeholder="Find by name or service ID"
          onSearch={onSearch}
        />
        {status === 'loading' && (
          <p role="status">Loading web services…{page && ' Showing the last loaded results.'}</p>
        )}
        {status === 'error' && (
          <EmptyState title="Unable to load web services." error>
            {error}
            {page && <p>Showing the last loaded results.</p>}
            <Button onClick={onRefresh}>Retry inventory</Button>
          </EmptyState>
        )}
        {status === 'success' && items.length === 0 && (
          <EmptyState title={search ? 'No matching services.' : 'No web services yet.'}>
            {!search && 'Create your first service using a container image.'}
          </EmptyState>
        )}
        {items.length > 0 && (
          <DataTable
            label="Web service inventory"
            items={items}
            itemKey={(item) => item.id}
            rowTestId={(item) => `resource-row-${item.id}`}
            columns={[
              {
                id: 'name',
                label: 'Service',
                render: (item) => (
                  <>
                    <ResourceLink href={resourceHref(item.id)} onNavigate={() => onOpen(item.id)}>
                      {item.name}
                    </ResourceLink>
                    <small>
                      {resourceGroups.find((group) => group.id === item.resourceGroupId)?.name ??
                        'Unknown group'}
                    </small>
                  </>
                ),
              },
              {
                id: 'image',
                label: 'Image',
                render: (item) => (
                  <code>
                    {item.spec.image.server}/{item.spec.image.name}:{item.spec.image.tag}
                  </code>
                ),
              },
              {
                id: 'placement',
                label: 'Deployments',
                render: (item) => (
                  <>
                    <StatusBadge tone="info">
                      {item.deployments.length} target{item.deployments.length === 1 ? '' : 's'}
                    </StatusBadge>
                    <small>{item.exposure.mode}</small>
                  </>
                ),
              },
              {
                id: 'actions',
                label: 'Actions',
                render: (item) => (
                  <div className="row-actions">
                    <Button onClick={() => onOpen(item.id, canManage)}>
                      {canManage ? 'Edit' : 'Inspect'}
                    </Button>
                    <Button
                      variant="danger"
                      disabled={deletingId === item.id || !canManage}
                      aria-label={`Delete ${item.name}`}
                      onClick={() => setDeleteCandidate(item)}
                    >
                      Delete
                    </Button>
                  </div>
                ),
              },
            ]}
          />
        )}
        <Pagination
          label="Services"
          page={page}
          pageNumber={pageNumber}
          pageSize={pageSize}
          onPage={onPage}
          onPageSize={onPageSize}
        />
      </section>
      {deleteCandidate && (
        <ConfirmationDialog
          title={`Delete ${deleteCandidate.name}?`}
          confirmLabel="Delete service"
          busy={deletingId === deleteCandidate.id}
          onCancel={() => setDeleteCandidate(null)}
          onConfirm={async () => {
            try {
              await onDelete(deleteCandidate.id)
              setDeleteCandidate(null)
            } catch {
              /* The manager exposes the API failure below. */
            }
          }}
        >
          <p>
            This requests deletion of the service and its deployments. Any open draft for this
            service will be discarded.
          </p>
          {deleteError && (
            <EmptyState title="Deletion failed." error>
              {deleteError}
            </EmptyState>
          )}
        </ConfirmationDialog>
      )}
    </div>
  )
}
