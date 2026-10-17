import { fetchSourceBuildJobs, fetchWebServiceReleases, retryWebServiceDeployment } from '../../api'
import { invalidateWebServiceState } from './invalidate'
import { useWebServiceQuery } from './useWebServiceQuery'
import { useApiScope } from '../../data/useApiScope'
import { useAppServices } from '../../shell/AppContext'
import { ResourceOperations } from '../../ui/ResourceOperations'
import { useVisibleRefresh } from '../../hooks/useVisibleRefresh'
import { Button, Notice, StatusBadge } from '../../ui/components'
import { useState } from 'react'
import { SourceBuildLogViewer } from '../../components/SourceBuildLogViewer'
import type { RealtimeSubscription } from '../../realtime/types'

export function WebServiceOperations({ id, active }: { id: string; active: boolean }) {
  const { api, navigation } = useAppServices()
  const origin = useApiScope(api)
  const { credential } = origin
  const [logJobId, setLogJobId] = useState<string | null>(null)
  const buildInterest: RealtimeSubscription[] = [
    { topic: 'source-builds', tenantId: api.tenantId, resourceId: id },
  ]
  const releaseInterest: RealtimeSubscription[] = [
    ...buildInterest,
    { topic: 'web-services', tenantId: api.tenantId, resourceId: id },
    { topic: 'operations', tenantId: api.tenantId, resourceId: id },
  ]
  const builds = useWebServiceQuery(
    ['source-builds', 'jobs', id],
    (signal) => fetchSourceBuildJobs(api.baseUrl, id, credential(), api.tenantId, signal),
    { active, realtime: buildInterest },
  )
  const releases = useWebServiceQuery(
    ['web-services', 'releases', id, 1],
    (signal) => fetchWebServiceReleases(api.baseUrl, id, credential(), api.tenantId, signal),
    { active, realtime: releaseInterest },
  )
  useVisibleRefresh(active, builds.refresh, 20000, buildInterest)
  useVisibleRefresh(active, releases.refresh, 20000, releaseInterest)
  return (
    <div className="resource-operations">
      <ResourceOperations
        filter={{ resourceType: 'pole/web-service', resourceId: id }}
        active={active}
        onAccepted={() => invalidateWebServiceState(origin)}
        onRetry={(operation, signal) => {
          const placement = operation.deployment.placement
          const desiredOperation = placement.desiredOperation
          if (desiredOperation !== 'Apply' && desiredOperation !== 'Delete')
            return Promise.reject(new Error('Refresh this operation before retrying.'))
          return retryWebServiceDeployment(
            api.baseUrl,
            id,
            operation.deployment.id,
            {
              expectedDeploymentRevision: placement.deploymentRevision!,
              expectedOperation: desiredOperation,
            },
            credential(),
            api.tenantId,
            signal,
          )
        }}
      />
      <section className="panel">
        <div className="panel__header">
          <h2 className="panel__title">Build operations</h2>
          <a
            className="button"
            href={navigation.resourceHref({
              resourceTypeId: 'pole/web-service',
              resourceId: id,
              section: 'builds',
            })}
          >
            Open builds
          </a>
          <Button onClick={() => void builds.refresh()}>Refresh builds</Button>
        </div>
        {active && logJobId && (
          <SourceBuildLogViewer
            jobId={logJobId}
            webServiceId={id}
            onClose={() => setLogJobId(null)}
          />
        )}
        {builds.error && (
          <Notice tone="error">Build state is unavailable. Previous results are stale.</Notice>
        )}
        {builds.resource?.items.map((job) => (
          <div key={job.id} className="operation-summary">
            <div>
              <strong>
                {job.branch} · {job.commitSha.slice(0, 8)}
              </strong>
              <p className="field__hint">
                {new Date(job.createdAtUtc).toLocaleString()} ·{' '}
                {job.state.failureCode ?? 'Build pipeline'}
              </p>
            </div>
            <div className="row-actions">
              <StatusBadge
                tone={
                  job.state.status === 'failed'
                    ? 'danger'
                    : job.state.status === 'succeeded'
                      ? 'success'
                      : 'info'
                }
              >
                {job.state.status}
              </StatusBadge>
              <Button
                onClick={() => setLogJobId(job.id)}
                aria-label={`View logs for build ${job.id}`}
              >
                View logs
              </Button>
            </div>
          </div>
        ))}
        {builds.resource?.items.length === 0 && <p>No persisted builds for this service.</p>}
        <p className="field__hint">
          Showing up to 20 current build records. Build success does not establish runtime
          readiness.
        </p>
      </section>
      <section className="panel">
        <div className="panel__header">
          <h2 className="panel__title">Release operations</h2>
          <a
            className="button"
            href={navigation.resourceHref({
              resourceTypeId: 'pole/web-service',
              resourceId: id,
              section: 'releases',
            })}
          >
            Open releases
          </a>
          <Button onClick={() => void releases.refresh()}>Refresh releases</Button>
        </div>
        {releases.error && (
          <Notice tone="error">Release state is unavailable. Previous results are stale.</Notice>
        )}
        {releases.resource?.releases.items.map((release) => (
          <div key={release.id} className="operation-summary">
            <div>
              <strong>
                {release.action} · revision {release.resourceRevision}
              </strong>
              <p className="field__hint">
                {new Date(release.requestedAtUtc).toLocaleString()} · {release.deployments.length}{' '}
                target{release.deployments.length === 1 ? '' : 's'}
              </p>
              {release.correlationId && (
                <p className="field__hint">
                  Correlation <code>{release.correlationId}</code>
                </p>
              )}
              {release.traceId && (
                <p className="field__hint">
                  Trace <code>{release.traceId}</code>
                </p>
              )}
            </div>
            <StatusBadge
              tone={
                release.outcome.includes('Failed')
                  ? 'danger'
                  : release.outcome === 'Succeeded'
                    ? 'success'
                    : 'info'
              }
            >
              {release.outcome}
            </StatusBadge>
          </div>
        ))}
        {releases.resource?.releases.items.length === 0 && (
          <p>No persisted releases for this service.</p>
        )}
        <p className="field__hint">
          Showing the latest 10 releases. Target apply outcomes are separate from observed health.
        </p>
      </section>
    </div>
  )
}
