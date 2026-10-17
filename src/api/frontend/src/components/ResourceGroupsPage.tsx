import { useState } from 'react'
import { Button, ConfirmationDialog, DataTable, EmptyState } from '../ui/components'
import { InventorySearch, Pagination } from '../ui/InventoryControls'
import { ResourceLink } from '../ui/ResourceLink'
import type { LoadStatus, PaginatedResponse, ResourceGroup } from '../types'

export function ResourceGroupsPage({
  items,
  page,
  status,
  error,
  pageNumber,
  pageSize,
  search,
  canManage,
  deletingId,
  deleteError,
  onRefresh,
  onCreate,
  onOpen,
  resourceHref,
  onDelete,
  onPage,
  onPageSize,
  onSearch,
}: {
  items: ResourceGroup[]
  page: PaginatedResponse<ResourceGroup> | null
  status: LoadStatus
  error: string
  pageNumber: number
  pageSize: number
  search: string
  canManage: boolean
  deletingId: string | null
  deleteError: string
  onRefresh: () => void
  onCreate: () => void
  onOpen: (id: string, edit?: boolean) => void
  resourceHref: (id: string) => string
  onDelete: (id: string) => Promise<void>
  onPage: (page: number) => void
  onPageSize: (size: number) => void
  onSearch: (search: string) => void
}) {
  const [deleting, setDeleting] = useState<ResourceGroup | null>(null)
  return (
    <div className="resource-group-catalog">
      <header className="ui-page-header">
        <div>
          <p className="ui-page-eyebrow">Resource groups</p>
          <h1>Environment boundaries</h1>
          <p>Organize resources, inspect group contents and manage metadata.</p>
        </div>
        <div className="row-actions">
          <Button aria-label="Refresh resource groups" onClick={onRefresh}>
            Refresh
          </Button>
          <Button variant="primary" disabled={!canManage} onClick={onCreate}>
            New group
          </Button>
        </div>
      </header>
      <section className="panel">
        <div className="panel__header">
          <h2 className="panel__title">All resource groups</h2>
          <span className="panel__meta">{page?.totalCount ?? 0} groups</span>
        </div>
        <InventorySearch search={search} placeholder="Find a group by name" onSearch={onSearch} />
        {status === 'loading' && <p role="status">Loading resource groups…</p>}
        {error && (
          <EmptyState title="Unable to load resource groups." error>
            {error}
            {page && <p>Showing the last loaded results.</p>}
            <Button onClick={onRefresh}>Retry inventory</Button>
          </EmptyState>
        )}
        {status === 'success' && items.length === 0 && (
          <EmptyState title={search ? 'No matching groups.' : 'No resource groups yet.'} />
        )}
        {items.length > 0 && (
          <DataTable
            label="Resource group inventory"
            items={items}
            itemKey={(group) => group.id}
            rowTestId={(group) => `resource-row-${group.id}`}
            columns={[
              {
                id: 'name',
                label: 'Group',
                render: (group) => (
                  <>
                    <ResourceLink href={resourceHref(group.id)} onNavigate={() => onOpen(group.id)}>
                      {group.name}
                    </ResourceLink>
                    <small>{group.description || 'No description'}</small>
                  </>
                ),
              },
              { id: 'resources', label: 'Resources', render: (group) => group.resourceCount },
              { id: 'tags', label: 'Tags', render: (group) => group.tags.join(', ') || 'No tags' },
              {
                id: 'actions',
                label: 'Actions',
                render: (group) => (
                  <div className="row-actions">
                    <Button onClick={() => onOpen(group.id, canManage)}>
                      {canManage ? 'Edit' : 'Inspect'}
                    </Button>
                    <Button
                      variant="danger"
                      disabled={!canManage || deletingId === group.id}
                      aria-label={`Delete ${group.name}`}
                      onClick={() => setDeleting(group)}
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
          label="Groups"
          page={page}
          pageNumber={pageNumber}
          pageSize={pageSize}
          onPage={onPage}
          onPageSize={onPageSize}
        />
      </section>
      {deleting && (
        <ConfirmationDialog
          title={`Delete ${deleting.name}?`}
          confirmLabel="Delete group"
          busy={deletingId === deleting.id}
          onCancel={() => setDeleting(null)}
          onConfirm={async () => {
            try {
              await onDelete(deleting.id)
              setDeleting(null)
            } catch {
              /* The API failure remains visible. */
            }
          }}
        >
          <p>
            A group can be deleted only after its resources and deployments are removed. An open
            metadata draft will be discarded.
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
