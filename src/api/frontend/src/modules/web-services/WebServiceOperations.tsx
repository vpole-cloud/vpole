import { fetchSourceBuildJobs, fetchWebServiceReleases, retryWebServiceDeployment } from '../../api'
import { useResource } from '../../hooks/useResource'
import { useAppServices } from '../../shell/AppContext'
import { ResourceOperations } from '../../ui/ResourceOperations'
import { useVisibleRefresh } from '../../hooks/useVisibleRefresh'
import { Button, Notice, StatusBadge } from '../../ui/components'

export function WebServiceOperations({ id, active }: { id: string; active: boolean }) {
  const { api, navigation } = useAppServices()
  const scope = `${api.baseUrl}:${api.tenantId}:${api.token}:${id}`
  const builds = useResource(`${scope}:jobs`, (signal) => fetchSourceBuildJobs(api.baseUrl, id, api.token, api.tenantId, signal))
  const releases = useResource(`${scope}:releases`, (signal) => fetchWebServiceReleases(api.baseUrl, id, api.token, api.tenantId, signal))
  useVisibleRefresh(active, builds.refresh)
  useVisibleRefresh(active, releases.refresh)
  return <div className="resource-operations"><ResourceOperations filter={{ resourceType: 'pole/web-service', resourceId: id }} active={active} onRetry={(operation, signal) => {
      const placement = operation.deployment.placement
      const desiredOperation = placement.desiredOperation
      if (desiredOperation !== 'Apply' && desiredOperation !== 'Delete') return Promise.reject(new Error('Refresh this operation before retrying.'))
      return retryWebServiceDeployment(api.baseUrl, id, operation.deployment.id, { expectedDeploymentRevision: placement.deploymentRevision!, expectedOperation: desiredOperation }, api.token, api.tenantId, signal)
    }} />
    <section className="panel"><div className="panel__header"><h2 className="panel__title">Build operations</h2><a className="button" href={navigation.resourceHref({ resourceTypeId: 'pole/web-service', resourceId: id, section: 'builds' })}>Open builds</a><Button onClick={() => void builds.refresh()}>Refresh builds</Button></div>
      {builds.error && <Notice tone="error">Build state is unavailable. Previous results are stale.</Notice>}
      {builds.resource?.items.map((job) => <div key={job.id} className="operation-summary"><div><strong>{job.branch} · {job.commitSha.slice(0, 8)}</strong><p className="field__hint">{new Date(job.createdAtUtc).toLocaleString()} · {job.state.failureCode ?? 'Build pipeline'}</p></div>
        <StatusBadge tone={job.state.status === 'failed' ? 'danger' : job.state.status === 'succeeded' ? 'success' : 'info'}>{job.state.status}</StatusBadge></div>)}
      {builds.resource?.items.length === 0 && <p>No persisted builds for this service.</p>}<p className="field__hint">Showing up to 20 current build records. Build success does not establish runtime readiness.</p></section>
    <section className="panel"><div className="panel__header"><h2 className="panel__title">Release operations</h2><a className="button" href={navigation.resourceHref({ resourceTypeId: 'pole/web-service', resourceId: id, section: 'releases' })}>Open releases</a><Button onClick={() => void releases.refresh()}>Refresh releases</Button></div>
      {releases.error && <Notice tone="error">Release state is unavailable. Previous results are stale.</Notice>}
      {releases.resource?.releases.items.map((release) => <div key={release.id} className="operation-summary"><div><strong>{release.action} · revision {release.resourceRevision}</strong><p className="field__hint">{new Date(release.requestedAtUtc).toLocaleString()} · {release.deployments.length} target{release.deployments.length === 1 ? '' : 's'}</p>
        {release.correlationId && <p className="field__hint">Correlation <code>{release.correlationId}</code></p>}{release.traceId && <p className="field__hint">Trace <code>{release.traceId}</code></p>}</div>
        <StatusBadge tone={release.outcome.includes('Failed') ? 'danger' : release.outcome === 'Succeeded' ? 'success' : 'info'}>{release.outcome}</StatusBadge></div>)}
      {releases.resource?.releases.items.length === 0 && <p>No persisted releases for this service.</p>}<p className="field__hint">Showing the latest 10 releases. Target apply outcomes are separate from observed health.</p></section>
  </div>
}
