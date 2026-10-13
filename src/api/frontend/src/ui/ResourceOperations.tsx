import { useState } from 'react'
import { fetchResourceOperations, type ResourceOperationsFilter } from '../modules/operations/api'
import { useScopedQuery } from '../data/useScopedQuery'
import { hasHealthyCurrentQueryData } from '../data/hasHealthyCurrentQueryData'
import { useApiScope } from '../data/useApiScope'
import { useScopedMutation } from '../hooks/useScopedMutation'
import { useAppServices } from '../shell/AppContext'
import type { ResourceDeploymentOperation } from '../types'
import { Button, ConfirmationDialog, EmptyState, Notice, StatusBadge } from './components'
import { ResourceHealthTarget } from './ResourceHealth'
import { ResourceRuntimeDiagnostics } from './ResourceRuntimeDiagnostics'
import { useVisibleRefresh } from '../hooks/useVisibleRefresh'
import { operationInterests } from '../realtime/resourceInterests'

export function ResourceOperations({ filter = {}, active = true, onRetry, onAccepted }: { filter?: ResourceOperationsFilter; active?: boolean; onRetry?: (operation: ResourceDeploymentOperation, signal: AbortSignal) => Promise<unknown>; onAccepted?: () => Promise<unknown> }) {
  const { api, tenant, navigation } = useAppServices()
  const { identity } = useApiScope(api)
  const scope = `${identity}:${filter.resourceGroupId}:${filter.resourceType}:${filter.resourceId}`
  return <ResourceOperationsContent key={scope} filter={filter} active={active} scope={scope} canManage={tenant.canManage} resourceHref={navigation.resourceHref} onRetry={onRetry} onAccepted={onAccepted} />
}

function ResourceOperationsContent({ filter, active, scope, canManage, resourceHref, onRetry, onAccepted }: {
  filter: ResourceOperationsFilter; active: boolean; scope: string; canManage: boolean;
  onAccepted?: () => Promise<unknown>;
  onRetry?: (operation: ResourceDeploymentOperation, signal: AbortSignal) => Promise<unknown>;
  resourceHref: (identity: { resourceTypeId: string; resourceId: string; section?: string }) => string | null
}) {
  const { api } = useAppServices()
  const apiScope = useApiScope(api)
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<ResourceDeploymentOperation | null>(null)
  const interest = operationInterests(api.tenantId, filter.resourceType, filter.resourceId)
  const operations = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: ['operations', filter.resourceGroupId ?? null, filter.resourceType ?? null, filter.resourceId ?? null, page],
    active, realtime: interest,
    load: (signal) => fetchResourceOperations(api.baseUrl, filter, page, apiScope.credential(), api.tenantId, signal) })
  const mutation = useScopedMutation(`${scope}:${canManage}`)
  useVisibleRefresh(active, operations.refresh, 20000, interest)
  const value = operations.resource
  const counts = value?.counts
  const metadataCurrent = hasHealthyCurrentQueryData(operations)
  const eligibleSelection = selected && metadataCurrent && value?.items.some(operation => operation.deployment.id === selected.deployment.id &&
    operation.retryable && operation.phase === 'Failed' && operation.deployment.placement.deploymentRevision === selected.deployment.placement.deploymentRevision &&
    operation.deployment.placement.desiredOperation === selected.deployment.placement.desiredOperation)
  if (value && operations.status === 'success' && page > Math.max(1, Math.ceil(value.totalCount / value.pageSize))) {
    setPage(Math.max(1, Math.ceil(value.totalCount / value.pageSize)))
  }
  return <section className="resource-operations" aria-label="Persistent operations"><div className="panel__header"><h2 className="panel__title">Operations and health</h2>
    <Button disabled={operations.status === 'loading'} onClick={() => void operations.refresh()}>Refresh operations</Button></div>
    <p className="field__hint">Operations come from saved server state. Closing this view stops observations; server work continues. Applied confirms configuration, Ready confirms observed pods.</p>
    {operations.error && <Notice tone="error">{operations.error} {value && 'Previous operation state is stale.'}</Notice>}
    {counts && <div className="health-counts" aria-label="Reconciliation totals"><span>Waiting {counts.waiting}</span><span>Accepted {counts.accepted}</span><span>Running {counts.running}</span><span>Applied {counts.applied}</span><span>Failed {counts.failed}</span></div>}
    {value && <p className="field__hint">{value.totalCount} persisted deployment{value.totalCount === 1 ? '' : 's'} in this scope. Runtime observations cover the {value.items.length} targets on this page. Refreshed {new Date(value.checkedAtUtc).toLocaleString()}.</p>}
    {!value && <EmptyState title={operations.status === 'error' ? 'Operations unavailable.' : 'Loading operations…'} />}
    {value?.items.length === 0 && <EmptyState title="No persisted deployments in this scope." />}
    {value?.items.map((operation) => {
      const deployment = operation.deployment
      const placement = deployment.placement
      const href = resourceHref({ resourceTypeId: deployment.resourceType, resourceId: deployment.resourceId })
      return <article key={deployment.id} data-testid={`operation-row-${deployment.id}`} className="operation-target"><header className="panel__header"><div><h3 className="panel__title">{href ? <a href={href}>{operation.resourceDisplayName ?? 'Resource'}</a> : operation.resourceDisplayName ?? 'Unavailable resource metadata'}</h3>
        <p className="field__hint">{placement.desiredOperation} · operation generation {operation.operationGeneration ?? placement.desiredGeneration} · desired {placement.desiredGeneration} · attempt {operation.attemptCount} · {new Date(operation.sinceUtc).toLocaleString()}</p></div>
        <div className="row-actions"><StatusBadge data-testid="operation-status" tone={operation.phase === 'Failed' ? 'danger' : operation.phase === 'Applied' ? 'success' : 'info'}>{operation.phase}</StatusBadge>
          {canManage && onRetry && operation.retryable && operation.phase === 'Failed' && <Button disabled={!metadataCurrent || mutation.busy} onClick={() => setSelected(operation)}>Retry this {placement.desiredOperation?.toLowerCase() ?? 'operation'}</Button>}</div></header>
        {placement.failureCode && <Notice tone="error">Apply outcome: {placement.failureCode}. Other targets are shown independently.</Notice>}
        {operation.retryNotBeforeUtc && <p className="field__hint">Automatic retry due after {new Date(operation.retryNotBeforeUtc).toLocaleString()}. A manual retry requests this exact failed operation.</p>}
        <ResourceHealthTarget placement={placement} active={active} targetName={operation.targetDisplayName ?? undefined} resourceType={deployment.resourceType} />
        <ResourceRuntimeDiagnostics operation={operation} active={active} />
      </article>
    })}
    {value && value.totalCount > value.pageSize && <div className="row-actions"><Button disabled={page === 1 || operations.status === 'loading'} onClick={() => setPage(page - 1)}>Previous targets</Button>
      <span>Page {page} of {Math.max(1, Math.ceil(value.totalCount / value.pageSize))}</span><Button disabled={page * value.pageSize >= value.totalCount || operations.status === 'loading'} onClick={() => setPage(page + 1)}>Next targets</Button></div>}
    {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}<Button onClick={() => { setSelected(null); void operations.refresh() }}>Refresh changed operation</Button></Notice>}
    {selected && canManage && onRetry && <ConfirmationDialog title={`Retry ${selected.deployment.placement.desiredOperation?.toLowerCase()} on this target?`} confirmLabel="Retry selected operation" busy={mutation.busy} confirmDisabled={!eligibleSelection} onCancel={() => setSelected(null)} onConfirm={() => {
      if (!eligibleSelection) return
      let guard = () => {}
      void mutation.run(async (signal) => {
        guard = () => { if (signal.aborted || !apiScope.isCurrent()) throw new DOMException('Aborted', 'AbortError') }
        guard(); const value = await onRetry(selected, signal); guard(); return { value }
      }, async () => {
        setSelected(null); await onAccepted?.(); guard(); await operations.refreshAfterWrite(); guard()
        await Promise.all(['operations', 'health', 'runtime-discovery', 'endpoints'].map(async (prefix) => {
          const queryKey = apiScope.key([prefix])
          await apiScope.session.client.cancelQueries({ queryKey }); guard()
          await apiScope.session.client.invalidateQueries({ queryKey, refetchType: 'active' })
        })); guard()
      }).catch(() => {})
    }}>Only deployment <code>{selected.deployment.id}</code> on target <code>{selected.deployment.placement.deploymentTargetId}</code> will be retried. Successful targets and other resources are untouched.
      {!eligibleSelection && <Notice>The selected operation changed or its current state is unavailable. Refresh operations and select a current failed operation.</Notice>}
    </ConfirmationDialog>}
  </section>
}
