// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { imageDescription, hasBuildImageAuthority } from './imageModel'
import { canEditDesiredConfiguration } from '../../data/gitOps'
import { useCallback, useState } from 'react'
import { fetchWebServiceReleases, redeployWebService, rollbackWebService } from '../../api'
import { useWebServiceQuery } from './useWebServiceQuery'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { useApiScope } from '../../data/useApiScope'
import { invalidateWebServiceState } from './invalidate'
import { useWebServiceMutation } from './useWebServiceMutation'
import { useVisibleRefresh } from '../../hooks/useVisibleRefresh'
import { useAppServices } from '../../shell/AppContext'
import type { WebService, WebServiceRelease, WebServiceSpec } from '../../types'
import { Button, ConfirmationDialog, EmptyState, Notice, StatusBadge } from '../../ui/components'
import type { RealtimeSubscription } from '../../realtime/types'

const image = (spec: WebServiceSpec) => imageDescription(spec)
function externalBuildUrl(value: string | null | undefined) {
  try {
    const url = new URL(value ?? '')
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null
  } catch {
    return null
  }
}
type Selection = {
  action: 'redeploy' | 'rollback'
  expectedRevision: number
  image: string
  target: WebServiceRelease | null
}

export function WebServiceReleases({
  service,
  onChanged,
}: {
  service: WebService
  onChanged: () => Promise<WebService | null>
}) {
  const { api } = useAppServices()
  const { identity } = useApiScope(api)
  const scope = `${identity}:${service.id}`
  return <ReleaseHistory key={scope} scope={scope} service={service} onChanged={onChanged} />
}

function ReleaseHistory({
  scope,
  service,
  onChanged,
}: {
  scope: string
  service: WebService
  onChanged: () => Promise<WebService | null>
}) {
  const { api, tenant, navigation, notifications } = useAppServices()
  const apiScope = useApiScope(api)
  const { credential, isCurrent } = apiScope
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<Selection | null>(null)
  const interest: RealtimeSubscription[] = [
    { topic: 'web-services', tenantId: api.tenantId, resourceId: service.id },
    { topic: 'operations', tenantId: api.tenantId, resourceId: service.id },
    { topic: 'source-builds', tenantId: api.tenantId, resourceId: service.id },
  ]
  const history = useWebServiceQuery(
    ['web-services', 'releases', service.id, page],
    (signal) =>
      fetchWebServiceReleases(api.baseUrl, service.id, credential(), api.tenantId, signal, {
        pageNumber: page,
      }),
    { realtime: interest },
  )
  const mutation = useWebServiceMutation(
    scope,
    canEditDesiredConfiguration(tenant.canManage, service.gitOps),
  )
  const value = history.resource
  const pending = value?.releases.items.some((release) => release.outcome === 'Pending') ?? false
  const { status: historyStatus, refresh: refreshHistory } = history
  const poll = useCallback(async () => {
    if (historyStatus !== 'loading') await refreshHistory()
  }, [historyStatus, refreshHistory])
  useVisibleRefresh(true, poll, pending ? 2500 : 20000, interest)
  if (
    value &&
    history.status === 'success' &&
    page > Math.max(1, Math.ceil(value.releases.totalCount / value.releases.pageSize))
  )
    setPage(Math.max(1, Math.ceil(value.releases.totalCount / value.releases.pageSize)))
  // A healthy background fetch does not invalidate this scoped revision context.
  const current =
    hasHealthyCurrentQueryData(history) && value?.activeRevision === service.activeRevision
  const hasDesiredTarget = service.deployments.some(
    (placement) => placement.desiredOperation !== 'Delete',
  )
  const writable =
    canEditDesiredConfiguration(tenant.canManage, service.gitOps) &&
    current &&
    !mutation.busy &&
    service.spec.image !== null &&
    hasDesiredTarget
  const resource = { resourceTypeId: 'pole/web-service', resourceId: service.id }
  const refresh = async () => {
    await Promise.all([history.refresh(), onChanged()])
  }
  const choose = (target: WebServiceRelease | null) => {
    if (!writable || !value || (target && hasBuildImageAuthority(service.spec))) return
    mutation.clearError()
    setSelected({
      action: target ? 'rollback' : 'redeploy',
      target,
      expectedRevision: value.activeRevision,
      image: image(target?.spec ?? service.spec),
    })
  }
  const refreshSelection = async () => {
    const [updatedHistory, updatedService] = await Promise.all([history.refresh(), onChanged()])
    if (
      !isCurrent() ||
      !selected ||
      !updatedHistory ||
      !updatedService ||
      updatedHistory.activeRevision !== updatedService.activeRevision
    )
      return
    setSelected({
      ...selected,
      expectedRevision: updatedHistory.activeRevision,
      image: image(selected.target?.spec ?? updatedService.spec),
    })
    mutation.clearError()
  }
  const eligibleSelection =
    selected &&
    writable &&
    (!selected.target || !hasBuildImageAuthority(service.spec)) &&
    current &&
    hasDesiredTarget &&
    selected.expectedRevision === value?.activeRevision &&
    (!selected.target ||
      (selected.target.outcome === 'Succeeded' &&
        selected.target.resourceRevision < value!.activeRevision))
  const error = mutation.error instanceof Error ? mutation.error.message : ''
  return (
    <section className="web-service-releases" aria-label="Release history and recovery">
      <div className="panel__header">
        <div>
          <h2 className="panel__title">Releases</h2>
          <p className="field__hint">
            Current desired revision {value?.activeRevision ?? service.activeRevision}. Release
            outcomes describe target apply; inspect readiness in Operations.
          </p>
        </div>
        <div className="row-actions">
          <a
            className="button"
            href={navigation.resourceHref({ ...resource, section: 'operations' })}
          >
            Open operations
          </a>
          <Button disabled={history.status === 'loading'} onClick={() => void refresh()}>
            Refresh releases
          </Button>
          {canEditDesiredConfiguration(tenant.canManage, service.gitOps) && (
            <Button disabled={!writable} onClick={() => choose(null)}>
              Redeploy current revision
            </Button>
          )}
        </div>
      </div>
      <p className="field__hint">
        History is saved on the server. Closing this view stops polling and leaves accepted recovery
        running.
      </p>
      {history.error && (
        <Notice tone="error">
          {history.error}{' '}
          {value &&
            `Showing previously loaded page ${value.releases.pageNumber}; its state is stale.`}
        </Notice>
      )}
      {value && value.activeRevision !== service.activeRevision && (
        <Notice>
          The service changed since its details were loaded. Refresh service and history before
          recovery.
        </Notice>
      )}
      {!tenant.canManage && (
        <Notice>Your Viewer role can inspect releases. Recovery requires a tenant writer.</Notice>
      )}
      {!value && (
        <EmptyState
          title={history.status === 'error' ? 'Release history unavailable.' : 'Loading releases…'}
        />
      )}
      {value?.releases.items.length === 0 && <EmptyState title="No recorded releases." />}
      {error && !selected && <Notice tone="error">{error}</Notice>}
      {value?.releases.items.map((release) => {
        const eligible =
          release.outcome === 'Succeeded' && release.resourceRevision < value.activeRevision
        const provenance = release.provenance
        const buildUrl = externalBuildUrl(provenance?.buildUrl)
        return (
          <article
            className="panel release-record"
            key={release.id}
            data-testid={`release-row-${release.id}`}
            aria-label={`Release revision ${release.resourceRevision}`}
          >
            <div className="panel__header">
              <div>
                <h3 className="panel__title">
                  Revision {release.resourceRevision} · {release.action}
                </h3>
                <p className="field__hint">
                  {release.resourceRevision === value.activeRevision
                    ? 'Current desired revision · '
                    : ''}
                  {new Date(release.requestedAtUtc).toLocaleString()} ·{' '}
                  {release.actorEmail ??
                    provenance?.deploymentTokenName ??
                    provenance?.actorType ??
                    release.actorUserId ??
                    'System'}
                </p>
              </div>
              <div className="row-actions">
                <StatusBadge
                  data-testid="release-status"
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
                {canEditDesiredConfiguration(tenant.canManage, service.gitOps) &&
                  eligible &&
                  !hasBuildImageAuthority(service.spec) && (
                    <Button disabled={!writable} onClick={() => choose(release)}>
                      Rollback to revision {release.resourceRevision}
                    </Button>
                  )}
              </div>
            </div>
            <p>
              <code>{image(release.spec)}</code>
            </p>
            <p className="field__hint">
              {Object.keys(release.spec.config.environmentVariables).length} literal environment
              entries ·{' '}
              {Object.keys(release.spec.config.connectionEnvironmentReferences ?? {}).length}{' '}
              connection references ·{' '}
              {Object.keys(release.spec.config.secretEnvironmentReferences ?? {}).length} Secret
              references · {release.spec.config.portMappings.length} ports
              {release.spec.autoscaling &&
                ` · autoscaling ${release.spec.autoscaling.minReplicas}–${release.spec.autoscaling.maxReplicas}`}
              . Values are hidden.
            </p>
            {provenance && (
              <div className="release-provenance">
                <p className="field__hint">
                  Source: {provenance.source}
                  {provenance.commitSha && (
                    <>
                      {' '}
                      · commit <code>{provenance.commitSha}</code>
                    </>
                  )}
                </p>
                {provenance.imageDigest && (
                  <p className="field__hint">
                    Recorded digest <code>{provenance.imageDigest}</code>. Deployment uses the image
                    tag.
                  </p>
                )}
                <div className="row-actions">
                  {provenance.buildJobId && (
                    <a
                      href={navigation.resourceHref({
                        ...resource,
                        section: 'builds',
                        fragment: `build-${encodeURIComponent(provenance.buildJobId)}`,
                      })}
                    >
                      Build {provenance.buildJobId}
                    </a>
                  )}
                  {buildUrl && (
                    <a href={buildUrl} target="_blank" rel="noreferrer">
                      Open CI run
                    </a>
                  )}
                </div>
              </div>
            )}
            <details>
              <summary>Target apply outcomes · {release.deployments.length}</summary>
              {release.deployments.map((deployment) => (
                <p
                  className="field__hint"
                  key={`${deployment.resourceDeploymentId}:${deployment.operation}`}
                >
                  Target <code>{deployment.deploymentTargetId}</code> · {deployment.operation} ·
                  generation {deployment.deploymentGeneration} · {deployment.outcome}
                  {deployment.failureCode && ` · ${deployment.failureCode}`}
                </p>
              ))}
              {release.deployments.some((deployment) => deployment.failureCode) && (
                <a href={navigation.resourceHref({ ...resource, section: 'operations' })}>
                  Inspect current targets and supported retry
                </a>
              )}
            </details>
            {release.outcome === 'Pending' && (
              <p className="field__hint">
                Pending can include automatic retries. Inspect current target failures in
                Operations.
              </p>
            )}
            {(release.traceId || release.correlationId) && (
              <details>
                <summary>Correlation identifiers</summary>
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
              </details>
            )}
            {release.completedAtUtc && (
              <p className="field__hint">
                Completed {new Date(release.completedAtUtc).toLocaleString()}.
              </p>
            )}
          </article>
        )
      })}
      {value && value.releases.totalCount > value.releases.pageSize && (
        <div className="row-actions">
          <Button
            disabled={value.releases.pageNumber <= 1 || history.status === 'loading'}
            onClick={() => setPage(value.releases.pageNumber - 1)}
          >
            Newer releases
          </Button>
          <span>
            Page {value.releases.pageNumber} of{' '}
            {Math.max(1, Math.ceil(value.releases.totalCount / value.releases.pageSize))}
          </span>
          <Button
            disabled={
              value.releases.pageNumber * value.releases.pageSize >= value.releases.totalCount ||
              history.status === 'loading'
            }
            onClick={() => setPage(value.releases.pageNumber + 1)}
          >
            Older releases
          </Button>
        </div>
      )}
      {selected && canEditDesiredConfiguration(tenant.canManage, service.gitOps) && (
        <ConfirmationDialog
          title={
            selected.target
              ? `Rollback to revision ${selected.target.resourceRevision}?`
              : `Redeploy revision ${selected.expectedRevision}?`
          }
          confirmLabel={selected.target ? 'Confirm rollback' : 'Confirm redeploy'}
          busy={mutation.busy}
          confirmDisabled={!eligibleSelection || Boolean(error)}
          onCancel={() => setSelected(null)}
          onConfirm={() => {
            if (!eligibleSelection || error) return
            void mutation
              .run(
                (signal) =>
                  selected.target
                    ? rollbackWebService(
                        api.baseUrl,
                        service.id,
                        {
                          targetRevision: selected.target.resourceRevision,
                          expectedRevision: selected.expectedRevision,
                        },
                        credential(),
                        api.tenantId,
                        signal,
                      )
                    : redeployWebService(
                        api.baseUrl,
                        service.id,
                        selected.expectedRevision,
                        credential(),
                        api.tenantId,
                        signal,
                      ),
                async (accepted, guard) => {
                  setSelected(null)
                  setPage(1)
                  notifications.publish(
                    `Recovery accepted as revision ${accepted.resourceRevision} for ${service.name}.`,
                  )
                  await onChanged()
                  guard()
                  await history.refreshAfterWrite()
                  guard()
                  await invalidateWebServiceState(apiScope)
                },
              )
              .catch(() => {})
          }}
        >
          <p>
            {selected.target
              ? `Restore the specification saved in revision ${selected.target.resourceRevision}`
              : `Apply the current specification from revision ${selected.expectedRevision}`}{' '}
            using <code>{selected.image}</code> across the current desired targets. This creates a
            new release.
          </p>
          <p>
            {selected.target
              ? 'Rollback restores the image reference, literal configuration, probes, autoscaling and logical dependency references. Those references must still be valid.'
              : 'Redeploy reapplies current configuration. It does not rebuild source code.'}
          </p>
          <p>
            Current targets, compute, fixed replicas, exposure, hostnames and tags remain current.
            Database contents and Secret values are not restored. An image tag may now refer to
            different image bytes.
          </p>
          {(error || !eligibleSelection) && (
            <Notice tone="error">
              {error || 'The selection changed. Refresh and review it again.'}
              <Button
                disabled={mutation.busy || history.status === 'loading'}
                onClick={() => void refreshSelection()}
              >
                Refresh recovery context
              </Button>
            </Notice>
          )}
        </ConfirmationDialog>
      )}
    </section>
  )
}
