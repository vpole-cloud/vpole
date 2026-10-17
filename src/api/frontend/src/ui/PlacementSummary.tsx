import type { ResourcePlacement } from '../types'
import { Notice, StatusBadge } from './components'

export function PlacementSummary({
  placement,
  targetName,
}: {
  placement: ResourcePlacement
  targetName?: string
}) {
  const current = placement.desiredGeneration === placement.observedGeneration
  return (
    <section className="panel">
      <div className="panel__header">
        <h2 className="panel__title">Deployment</h2>
        <StatusBadge
          tone={
            placement.failureCode
              ? 'danger'
              : placement.status === 'Applied' && current
                ? 'success'
                : 'info'
          }
        >
          {placement.status}
        </StatusBadge>
      </div>
      <dl className="web-service-facts">
        <div>
          <dt>Target</dt>
          <dd>{targetName ?? placement.deploymentTargetId}</dd>
        </div>
        <div>
          <dt>Compute</dt>
          <dd>
            {placement.compute.displayName} · {placement.compute.requests.cpuMillis} mCPU ·{' '}
            {placement.compute.requests.memoryMi} MiB requested
          </dd>
        </div>
        <div>
          <dt>Generation</dt>
          <dd>
            {placement.observedGeneration} observed / {placement.desiredGeneration} desired
          </dd>
        </div>
        <div>
          <dt>Operation</dt>
          <dd>{placement.desiredOperation}</dd>
        </div>
        {placement.endpoints.map((endpoint) => (
          <div key={`${endpoint.kind}:${endpoint.host}:${endpoint.port}`}>
            <dt>{endpoint.kind} endpoint</dt>
            <dd>
              <code>
                {endpoint.host}:{endpoint.port}
              </code>
            </dd>
          </div>
        ))}
      </dl>
      {placement.failureCode && (
        <Notice tone="error">Reconciliation failed: {placement.failureCode}</Notice>
      )}
      <p className="field__hint">
        Applied reports workload reconciliation. It does not confirm that the database accepts
        application connections.
      </p>
    </section>
  )
}
