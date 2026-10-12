import { useEffect, useState } from 'react'
import { useVisibleRefresh } from '../hooks/useVisibleRefresh'
import { fetchResourceObservation } from '../modules/operations/api'
import { useScopedQuery } from '../data/useScopedQuery'
import { useApiScope } from '../data/useApiScope'
import { useAppServices } from '../shell/AppContext'
import type { ResourcePlacement } from '../types'
import { Button, DataTable, Notice, StatusBadge } from './components'

const reasons: Record<string, string> = {
  Unsupported: 'This provider does not publish runtime observations.', Forbidden: 'The target credentials cannot read this workload.',
  NotFound: 'No owned native workload was found. Provisioning or cleanup may still be in progress.', OwnershipMismatch: 'Native ownership could not be verified.',
  TargetUnavailable: 'The selected target is unavailable.', ClusterUnavailable: 'The cluster could not be reached.',
  Timeout: 'The observation timed out.', Superseded: 'A newer deployment revision replaced this observation. Refresh for the current state.', ResourceNotFound: 'The logical resource is no longer available.',
}

export function ResourceHealth({ placements, active, targetNames = {} }: {
  placements: ResourcePlacement[]; active: boolean; targetNames?: Record<string, string>
}) {
  return <section className="resource-health" aria-label="Observed resource health"><h2 className="panel__title">Runtime readiness</h2>
    <p className="field__hint">Observed pod readiness is separate from reconciliation and application connectivity. Each target is checked independently.</p>
    {placements.length === 0 && <Notice>No current deployment is known.</Notice>}
    {active && placements.map((placement) => <ResourceHealthTarget key={placement.deploymentId} placement={placement} active={active}
      targetName={targetNames[placement.deploymentTargetId]} />)}
  </section>
}

export function ResourceHealthTarget({ placement, active, targetName }: {
  placement: ResourcePlacement; active: boolean; targetName?: string
}) {
  const { api } = useAppServices()
  const { credential } = useApiScope(api)
  const details = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: ['health', placement.deploymentId, placement.deploymentRevision, placement.desiredGeneration],
    load: (signal) => fetchResourceObservation(api.baseUrl, placement.deploymentId, credential(), api.tenantId, signal) })
  useVisibleRefresh(active, details.refresh)
  const [now, setNow] = useState(Date.now)
  useEffect(() => { if (!active) return; const timer = window.setInterval(() => setNow(Date.now()), 10000); return () => window.clearInterval(timer) }, [active])
  const result = details.resource
  const fresh = result && result.deploymentRevision === (placement.deploymentRevision ?? 1) && result.desiredGeneration === placement.desiredGeneration
  const value = fresh ? result.observation : null
  const stale = details.status === 'error' || !fresh || Boolean(result && now - Date.parse(result.checkedAtUtc) > 45000)
  const observed = value?.kind === 'Observed' ? value : null
  const ready = !stale && observed?.ready && placement.status === 'Applied' && placement.observedGeneration === placement.desiredGeneration
  return <article data-testid={`health-target-${placement.deploymentId}`} className="panel health-target" aria-label={`Runtime target ${targetName ?? placement.deploymentTargetId}`}>
    <div className="panel__header"><h3 className="panel__title">{targetName ?? `Target ${placement.deploymentTargetId}`}</h3>
      <div className="row-actions"><StatusBadge data-testid="readiness-status" tone={ready ? 'success' : stale ? 'warning' : 'info'}>
        {stale && result ? 'Stale observation' : ready ? 'Ready' : observed ? observed.rolloutCurrent ? 'Not ready' : 'Rollout in progress' : details.status === 'loading' ? 'Checking…' : 'Readiness unavailable'}
      </StatusBadge><Button disabled={details.status === 'loading'} onClick={() => void details.refresh()}>Refresh readiness</Button></div></div>
    <p className="field__hint">Reconciliation: {placement.status} · desired generation {placement.desiredGeneration} · apply confirmed {placement.observedGeneration}.
      {result && <> Last observed {new Date(result.checkedAtUtc).toLocaleString()}.</>}</p>
    {details.error && <Notice tone="error">{details.error} Previous evidence is stale; readiness is unknown.</Notice>}
    {value?.kind === 'Unavailable' && <Notice>{reasons[value.reason] ?? 'Runtime observation is unavailable.'}</Notice>}
    {observed && <><dl className="web-service-facts"><div><dt>Desired / current</dt><dd>{observed.desiredReplicas} / {observed.currentReplicas}</dd></div>
      <div><dt>Updated</dt><dd>{observed.updatedReplicas}</dd></div><div><dt>Ready / available</dt><dd>{observed.readyReplicas} / {observed.availableReplicas}</dd></div>
      <div><dt>Restarts</dt><dd>{observed.pods.reduce((total, pod) => total + pod.restartCount, 0)}</dd></div></dl>
      {observed.podsTruncated && <Notice>Pod results are limited. Complete rollout readiness has not been established.</Notice>}
      <details><summary>Pods and warnings · {observed.pods.length} observed</summary>
        <DataTable items={observed.pods} itemKey={(pod) => pod.name} label="Observed pods" columns={[
          { id: 'name', label: 'Pod', render: (pod) => <code>{pod.name}</code> },
          { id: 'state', label: 'State', render: (pod) => `${pod.phase} · ${pod.ready ? 'ready' : 'not ready'}${pod.currentGeneration ? '' : ' · earlier generation'}` },
          { id: 'restarts', label: 'Restarts', render: (pod) => pod.restartCount },
          { id: 'reason', label: 'Reason', render: (pod) => pod.reasons.join(', ') || '—' },
        ]} />
        {observed.events.map((event, index) => <p key={`${event.reason}:${index}`} className="field__hint">{event.reason} · {event.count} occurrence{event.count === 1 ? '' : 's'}{event.lastSeenAtUtc && ` · ${new Date(event.lastSeenAtUtc).toLocaleString()}`}</p>)}
        {observed.eventsUnavailableReason && <Notice>Warnings are unavailable ({observed.eventsUnavailableReason}). Pod observations are shown separately.</Notice>}
      </details></>}
  </article>
}
