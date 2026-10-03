import { useState } from 'react'
import { Button, ConfirmationDialog, DataTable, EmptyState, Field, StatusBadge } from '../console/components'
import type { LoadStatus, ResourceGroup, WebService } from '../types'

export function WebServicesPage({ items, resourceGroups, status, error, deletingId, deleteError, canManage, onRefresh, onCreate, onOpen, onDelete }:
  { items: WebService[]; resourceGroups: ResourceGroup[]; status: LoadStatus; error?: string; deletingId: string | null;
    deleteError?: string; canManage: boolean; onRefresh: () => void; onCreate: () => void;
    onOpen: (id: string, edit?: boolean) => void; onDelete: (id: string) => Promise<void> }) {
  const [query, setQuery] = useState('')
  const [deleteCandidate, setDeleteCandidate] = useState<WebService | null>(null)
  const filteredItems = items.filter((item) => [item.name, item.spec.image.server, item.spec.image.name,
    ...item.deployments.flatMap((deployment) => deployment.ingress?.hostnames ?? [])].join(' ').toLowerCase().includes(query.trim().toLowerCase()))

  return <div className="web-service-catalog">
    <header className="console-page-header"><div><h1>Web services</h1><p>Open a service to inspect its configuration, deployments and builds.</p></div>
      <div className="row-actions"><Button aria-label="Refresh web services" onClick={onRefresh}>Refresh</Button>
        <Button variant="primary" disabled={!canManage} onClick={onCreate}>New service</Button></div></header>
    <section className="panel resource-groups-list-panel"><div className="panel__header">
      <h2 className="panel__title">All web services</h2><span className="panel__meta">{filteredItems.length} visible</span></div>
      <Field label="Search"><input className="field__control" value={query} onChange={(event) => setQuery(event.target.value)}
        placeholder="Find by name, domain, or image" /></Field>
      {status === 'loading' && <EmptyState title="Loading web services…" />}
      {status === 'error' && <EmptyState title="Unable to load web services." error>{error}</EmptyState>}
      {status === 'success' && filteredItems.length === 0 && <EmptyState title={items.length ? 'No matching services.' : 'No web services yet.'}>
        {!items.length && 'Create your first service using a container image.'}</EmptyState>}
      {status === 'success' && filteredItems.length > 0 && <DataTable label="Web service inventory" items={filteredItems} itemKey={(item) => item.id} columns={[
        { id: 'name', label: 'Service', render: (item) => <><Button className="console-resource-link" onClick={() => onOpen(item.id)}>{item.name}</Button>
          <small>{resourceGroups.find((group) => group.id === item.resourceGroupId)?.name ?? 'Unknown group'}</small></> },
        { id: 'image', label: 'Image', render: (item) => <code>{item.spec.image.server}/{item.spec.image.name}:{item.spec.image.tag}</code> },
        { id: 'placement', label: 'Deployments', render: (item) => <><StatusBadge tone="info">{item.deployments.length} target{item.deployments.length === 1 ? '' : 's'}</StatusBadge>
          <small>{item.exposure.mode}</small></> },
        { id: 'actions', label: 'Actions', render: (item) => <div className="row-actions"><Button onClick={() => onOpen(item.id, canManage)}>{canManage ? 'Edit' : 'Inspect'}</Button>
          <Button variant="danger" disabled={deletingId === item.id || !canManage} aria-label={`Delete ${item.name}`} onClick={() => setDeleteCandidate(item)}>Delete</Button></div> },
      ]} />}
    </section>
    {deleteCandidate && <ConfirmationDialog title={`Delete ${deleteCandidate.name}?`} confirmLabel="Delete service" busy={deletingId === deleteCandidate.id}
      onCancel={() => setDeleteCandidate(null)} onConfirm={async () => {
        try { await onDelete(deleteCandidate.id); setDeleteCandidate(null) } catch { /* The manager exposes the API failure below. */ }
      }}><p>This requests deletion of the service and its deployments. Any open draft for this service will be discarded.</p>
      {deleteError && <EmptyState title="Deletion failed." error>{deleteError}</EmptyState>}
    </ConfirmationDialog>}
  </div>
}
