import { useEffect, useState } from 'react'
import { fetchResourceGroups } from '../../api'
import { usePagedInventory } from '../../hooks/usePagedInventory'
import { useAppServices } from '../../shell/AppContext'
import { validResourceId } from '../../shell/workspaceState'
import { Button, EmptyState } from '../../ui/components'
import { Pagination } from '../../ui/InventoryControls'
import { ResourceLink } from '../../ui/ResourceLink'
import { GroupResources } from './GroupResources'

interface TreeState { open: boolean; page: number; pageSize: number; expanded: string[] }
function readTree(key: string): TreeState {
  try {
    const value = JSON.parse(localStorage.getItem(key) ?? 'null')
    return { open: typeof value?.open === 'boolean' ? value.open : !window.matchMedia?.('(max-width: 760px)').matches,
      page: Number.isSafeInteger(value?.page) && value.page > 0 && value.page <= 1_000_000 ? value.page : 1,
      pageSize: Number.isSafeInteger(value?.pageSize) && value.pageSize > 0 && value.pageSize <= 100 ? value.pageSize : 8,
      expanded: Array.isArray(value?.expanded) ? [...new Set<string>(value.expanded.filter((id: unknown) => typeof id === 'string' && validResourceId(id)).slice(0, 50))] : [] }
  } catch { return { open: !window.matchMedia?.('(max-width: 760px)').matches, page: 1, pageSize: 8, expanded: [] } }
}

function TreeResources({ groupId, refreshVersion }: { groupId: string; refreshVersion: number }) {
  const [page, setPage] = useState(1)
  const [size, setSize] = useState(8)
  return <GroupResources groupId={groupId} compact pageNumber={page} pageSize={size} refreshVersion={refreshVersion}
    onPage={setPage} onPageSize={(value) => { setSize(value); setPage(1) }} />
}

export function ResourceTree() {
  const { api, auth, navigation, resourceVersion } = useAppServices()
  const key = `pole.resource-tree.v1.${auth.userId}.${api.tenantId}`
  const [state, setState] = useState(() => readTree(key))
  const pageSize = state.pageSize
  const inventory = usePagedInventory({ scopeKey: `${api.baseUrl}:${api.token}:${api.tenantId}`, enabled: state.open,
    queryKey: JSON.stringify([state.page, pageSize, resourceVersion]),
    load: (signal) => fetchResourceGroups(api.baseUrl, api.token, api.tenantId, signal, { pageNumber: state.page, pageSize }), getId: (group) => group.id })
  useEffect(() => { try { localStorage.setItem(key, JSON.stringify(state)) } catch { /* Navigation works without storage. */ } }, [key, state])
  const lastPage = Math.max(1, Math.ceil((inventory.page?.totalCount ?? 0) / pageSize))
  if (inventory.status === 'success' && inventory.isCurrentPage && state.page > lastPage) setState({ ...state, page: lastPage })
  return <details className="resource-tree" open={state.open} onToggle={(event) => {
    const open = event.currentTarget.open
    setState((current) => current.open === open ? current : { ...current, open })
  }}>
    <summary>Resources</summary>
    {state.open && <nav aria-label="Resource tree">
      <Button aria-label="Refresh resource tree" onClick={() => void inventory.refresh()}>Refresh</Button>
      {inventory.status === 'loading' && <p role="status">Loading groups…</p>}
      {inventory.error && <EmptyState title="Unable to load resource tree." error>{inventory.error}</EmptyState>}
      {inventory.status === 'success' && inventory.items.length === 0 && <p>No resource groups yet.</p>}
      <ul className="resource-tree__groups">{inventory.items.map((group) => {
        const expanded = state.expanded.includes(group.id)
        const resource = { resourceTypeId: 'pole/resource-group', resourceId: group.id }
        return <li key={group.id}><div className="resource-tree__group">
          <Button aria-label={`${expanded ? 'Collapse' : 'Expand'} ${group.name}`} aria-expanded={expanded}
            onClick={() => setState((current) => ({ ...current, expanded: expanded ? current.expanded.filter((id) => id !== group.id) : [...current.expanded, group.id].slice(-50) }))}>{expanded ? '−' : '+'}</Button>
          <ResourceLink href={navigation.resourceHref(resource)} onNavigate={() => navigation.openResource(resource)}>{group.name}</ResourceLink>
          <small>{group.resourceCount}</small></div>
          {expanded && <TreeResources groupId={group.id} refreshVersion={resourceVersion} />}
        </li>
      })}</ul>
      <Pagination label="Tree groups" compact page={inventory.page} pageNumber={state.page} pageSize={pageSize}
        onPage={(page) => setState((current) => ({ ...current, page }))} onPageSize={(pageSize) => setState((current) => ({ ...current, pageSize, page: 1 }))} />
    </nav>}
  </details>
}
