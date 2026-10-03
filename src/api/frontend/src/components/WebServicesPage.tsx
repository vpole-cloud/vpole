import { useState } from 'react'
import { Button, ConfirmationDialog, DataTable, EmptyState, Field, StatusBadge } from '../console/components'
import type { LoadStatus, PaginatedResponse, ResourceGroup, WebService } from '../types'

export function WebServicesPage({ items, resourceGroups, status, error, page, pageNumber, pageSize, search, onPage, onPageSize, onSearch, deletingId, deleteError, canManage, onRefresh, onCreate, onOpen, onDelete }:
  { items: WebService[]; resourceGroups: ResourceGroup[]; status: LoadStatus; error?: string; deletingId: string | null;
    page: PaginatedResponse<WebService> | null; pageNumber: number; pageSize: number; search: string;
    onPage: (pageNumber: number) => void; onPageSize: (pageSize: number) => void; onSearch: (search: string) => void;
    deleteError?: string; canManage: boolean; onRefresh: () => void; onCreate: () => void;
    onOpen: (id: string, edit?: boolean) => void; onDelete: (id: string) => Promise<void> }) {
  const [query, setQuery] = useState(search)
  const [currentSearch, setCurrentSearch] = useState(search)
  if (currentSearch !== search) { setCurrentSearch(search); setQuery(search) }
  const [deleteCandidate, setDeleteCandidate] = useState<WebService | null>(null)
  const totalPages = Math.max(1, Math.ceil((page?.totalCount ?? 0) / pageSize))
  const loadedTotalPages = page ? Math.max(1, Math.ceil(page.totalCount / page.pageSize)) : totalPages

  return <div className="web-service-catalog">
    <header className="console-page-header"><div><h1>Web services</h1><p>Open a service to inspect its configuration, deployments and builds.</p></div>
      <div className="row-actions"><Button aria-label="Refresh web services" onClick={onRefresh}>Refresh</Button>
        <Button variant="primary" disabled={!canManage} onClick={onCreate}>New service</Button></div></header>
    <section className="panel resource-groups-list-panel"><div className="panel__header">
      <h2 className="panel__title">All web services</h2><span className="panel__meta">{page ? `${page.totalCount} service${page.totalCount === 1 ? '' : 's'}` : 'Inventory'}</span></div>
      <form className="inventory-search" onSubmit={(event) => { event.preventDefault(); onSearch(query.trim()) }}>
        <Field label="Search"><input className="field__control" value={query} onChange={(event) => setQuery(event.target.value)}
          placeholder="Find by name or service ID" /></Field><Button type="submit">Search</Button>
      </form>
      {status === 'loading' && <p role="status">Loading web services…{page && ' Showing the last loaded results.'}</p>}
      {status === 'error' && <EmptyState title="Unable to load web services." error>{error}
        {page && <p>Showing the last loaded results.</p>}<Button onClick={onRefresh}>Retry inventory</Button></EmptyState>}
      {status === 'success' && items.length === 0 && <EmptyState title={search ? 'No matching services.' : 'No web services yet.'}>
        {!search && 'Create your first service using a container image.'}</EmptyState>}
      {items.length > 0 && <DataTable label="Web service inventory" items={items} itemKey={(item) => item.id} columns={[
        { id: 'name', label: 'Service', render: (item) => <><Button className="console-resource-link" onClick={() => onOpen(item.id)}>{item.name}</Button>
          <small>{resourceGroups.find((group) => group.id === item.resourceGroupId)?.name ?? 'Unknown group'}</small></> },
        { id: 'image', label: 'Image', render: (item) => <code>{item.spec.image.server}/{item.spec.image.name}:{item.spec.image.tag}</code> },
        { id: 'placement', label: 'Deployments', render: (item) => <><StatusBadge tone="info">{item.deployments.length} target{item.deployments.length === 1 ? '' : 's'}</StatusBadge>
          <small>{item.exposure.mode}</small></> },
        { id: 'actions', label: 'Actions', render: (item) => <div className="row-actions"><Button onClick={() => onOpen(item.id, canManage)}>{canManage ? 'Edit' : 'Inspect'}</Button>
          <Button variant="danger" disabled={deletingId === item.id || !canManage} aria-label={`Delete ${item.name}`} onClick={() => setDeleteCandidate(item)}>Delete</Button></div> },
      ]} />}
      <nav className="inventory-pagination" aria-label="Web service pages">
        <Field label="Per page"><select className="field__control" aria-label="Services per page" value={pageSize}
          onChange={(event) => onPageSize(Number(event.target.value))}>
          {[...new Set([8, 20, 50, pageSize])].sort((a, b) => a - b).map((size) => <option key={size} value={size}>{size}</option>)}
        </select></Field>
        <span aria-live="polite">{`Page ${page?.pageNumber ?? pageNumber} of ${loadedTotalPages}`}</span>
        <div className="row-actions"><Button disabled={pageNumber <= 1} onClick={() => onPage(pageNumber - 1)}>Previous page</Button>
          <Button disabled={!page || pageNumber >= totalPages} onClick={() => onPage(pageNumber + 1)}>Next page</Button></div>
      </nav>
    </section>
    {deleteCandidate && <ConfirmationDialog title={`Delete ${deleteCandidate.name}?`} confirmLabel="Delete service" busy={deletingId === deleteCandidate.id}
      onCancel={() => setDeleteCandidate(null)} onConfirm={async () => {
        try { await onDelete(deleteCandidate.id); setDeleteCandidate(null) } catch { /* The manager exposes the API failure below. */ }
      }}><p>This requests deletion of the service and its deployments. Any open draft for this service will be discarded.</p>
      {deleteError && <EmptyState title="Deletion failed." error>{deleteError}</EmptyState>}
    </ConfirmationDialog>}
  </div>
}
