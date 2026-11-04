import { canEditDesiredConfiguration } from '../../data/gitOps'
import { eligibilityKeys } from '../../data/queryKeys'
import { useCallback, useEffect, useState } from 'react'
import {
  addWebServiceHostnameBinding,
  deleteWebServiceHostnameBinding,
  fetchAvailableDeploymentTargets,
  fetchDomainZones,
  fetchWebServiceHostnameBindings,
} from '../../api'
import { useWebServiceQuery } from './useWebServiceQuery'
import { useApiScope } from '../../data/useApiScope'
import { useVisibleRefresh } from '../../hooks/useVisibleRefresh'
import { invalidateWebServiceState } from './invalidate'
import { useWebServiceMutation } from './useWebServiceMutation'
import { useAppServices } from '../../shell/AppContext'
import type {
  AvailableDeploymentTarget,
  HostnameBinding,
  TenantDomainZone,
  WebService,
  WebServiceDeployment,
} from '../../types'
import {
  Button,
  ConfirmationDialog,
  EmptyState,
  Field,
  Notice,
  StatusBadge,
} from '../../ui/components'
import { domainZoneHref } from '../domains/navigation'
import { domainInterests } from '../domains/realtime'

export function WebServiceDomains({
  service,
  active,
  onDirtyChange,
  onChanged,
}: {
  service: WebService
  active: boolean
  onDirtyChange: (dirty: boolean) => void
  onChanged: () => Promise<void>
}) {
  const { api, tenant, navigation } = useAppServices()
  const { identity, credential } = useApiScope(api)
  const scope = `${identity}:${service.id}`
  const zones = useWebServiceQuery(
    ['domains', 'zones'],
    (signal) => fetchDomainZones(api.baseUrl, credential(), api.tenantId, signal),
    { active, realtime: domainInterests(api.tenantId) },
  )
  const targets = useWebServiceQuery(
    eligibilityKeys.targets('pole/web-service', service.isolationScopeId, false),
    (signal) =>
      fetchAvailableDeploymentTargets(
        api.baseUrl,
        service.isolationScopeId ?? undefined,
        credential(),
        api.tenantId,
        signal,
        false,
      ),
    { active, realtime: [{ topic: 'deployment-target-eligibility', tenantId: api.tenantId }] },
  )
  // A healthy background read retains usable metadata in this exact query scope.
  const zonesAvailable =
    zones.isCurrent && zones.resource !== null && zones.status !== 'error' && !zones.error
  const [dirtyTargets, setDirtyTargets] = useState<Record<string, boolean>>({})
  const setDirty = useCallback(
    (id: string, dirty: boolean) =>
      setDirtyTargets((current) => (current[id] === dirty ? current : { ...current, [id]: dirty })),
    [],
  )
  const dirty = service.deployments.some((deployment) => dirtyTargets[deployment.deploymentId])
  useEffect(() => {
    onDirtyChange(dirty)
    return () => onDirtyChange(false)
  }, [dirty, onDirtyChange])
  const counts = service.deployments.reduce(
    (summary, deployment) => ({
      ...summary,
      [deployment.status]: (summary[deployment.status] ?? 0) + 1,
    }),
    {} as Record<string, number>,
  )
  return (
    <div className="web-service-domains">
      <section className="panel">
        <header className="ui-page-header">
          <div>
            <p className="ui-page-eyebrow">Exposure</p>
            <h2>Domains and hostnames</h2>
            <p>Every hostname belongs to one deployment target. Targets reconcile independently.</p>
          </div>
          <a className="button" href={domainZoneHref('catalog', api.tenantId)}>
            Browse domain zones
          </a>
        </header>
        <p>
          {Object.entries(counts)
            .map(([status, count]) => `${count} ${status}`)
            .join(' · ')}
        </p>
        {service.exposure.mode === 'Private' && (
          <Notice>
            This service is private.{' '}
            <a
              href={navigation.resourceHref({
                resourceTypeId: 'pole/web-service',
                resourceId: service.id,
                section: 'configuration',
              })}
            >
              Enable public exposure in Configuration
            </a>{' '}
            before attaching a hostname.
          </Notice>
        )}
        {!tenant.canManage && <Notice>Tenant viewer access is read-only.</Notice>}
        {[zones.error, targets.error].filter(Boolean).map((error, index) => (
          <Notice key={index} tone="error">
            {error}
          </Notice>
        ))}
        <div className="row-actions">
          <Button
            onClick={() => {
              void zones.refresh()
              void targets.refresh()
              void onChanged()
            }}
          >
            Refresh zones and targets
          </Button>
          {canEditDesiredConfiguration(tenant.canManage, service.gitOps) && (
            <a className="button" href={domainZoneHref('create', api.tenantId)}>
              Reserve a zone
            </a>
          )}
        </div>
        <p className="field__hint">
          An accepted alias reserves ownership and requests ingress configuration. Applied does not
          establish external DNS, certificate readiness or application reachability.
        </p>
      </section>
      {service.deployments.map((deployment) => (
        <DeploymentDomains
          key={`${scope}:${deployment.deploymentId}`}
          service={service}
          deployment={deployment}
          active={active}
          canManage={canEditDesiredConfiguration(tenant.canManage, service.gitOps)}
          zones={zones.resource ?? []}
          zonesAvailable={zonesAvailable}
          target={targets.resource?.find((target) => target.id === deployment.deploymentTargetId)}
          onDirtyChange={setDirty}
          onChanged={onChanged}
        />
      ))}
    </div>
  )
}

function DeploymentDomains({
  service,
  deployment,
  active,
  canManage,
  zones,
  zonesAvailable,
  target,
  onDirtyChange,
  onChanged,
}: {
  service: WebService
  deployment: WebServiceDeployment
  active: boolean
  canManage: boolean
  zones: TenantDomainZone[]
  zonesAvailable: boolean
  target?: AvailableDeploymentTarget
  onDirtyChange: (id: string, dirty: boolean) => void
  onChanged: () => Promise<void>
}) {
  const { api } = useAppServices()
  const apiScope = useApiScope(api)
  const { identity, credential, isCurrent } = apiScope
  const scope = `${identity}:${service.id}:${deployment.deploymentId}`
  const bindings = useWebServiceQuery(
    [
      'web-services',
      'hostnames',
      service.id,
      deployment.deploymentId,
      deployment.deploymentRevision,
      deployment.desiredGeneration,
    ],
    (signal) =>
      fetchWebServiceHostnameBindings(
        api.baseUrl,
        service.id,
        deployment.deploymentId,
        credential(),
        api.tenantId,
        signal,
      ),
    { active, realtime: domainInterests(api.tenantId) },
  )
  const mutation = useWebServiceMutation(scope, canManage)
  const [zoneId, setZoneId] = useState('')
  const [relativeName, setRelativeName] = useState('')
  const [technicalName, setTechnicalName] = useState('')
  const [editing, setEditing] = useState<HostnameBinding | null>(null)
  const [removing, setRemoving] = useState<HostnameBinding | null>(null)
  const [notice, setNotice] = useState('')
  const dirty = Boolean(zoneId || relativeName || technicalName || editing)
  useEffect(() => {
    onDirtyChange(deployment.deploymentId, dirty)
    return () => onDirtyChange(deployment.deploymentId, false)
  }, [deployment.deploymentId, dirty, onDirtyChange])
  const refreshBindings = bindings.refresh
  useVisibleRefresh(
    active && ['WaitingForRuntimeCell', 'Pending', 'Reconciling'].includes(deployment.status),
    refreshBindings,
    2500,
    domainInterests(api.tenantId),
  )
  const eligible = zones.filter(
    (zone) =>
      zone.deploymentTargetId === deployment.deploymentTargetId &&
      zone.kind === 'managed-reservation',
  )
  const selected = eligible.find((zone) => zone.id === zoneId)
  const validTechnicalName = /^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(technicalName.trim())
  const validName =
    relativeName.trim() === '@' ||
    relativeName
      .trim()
      .toLowerCase()
      .split('.')
      .every((label) => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label))
  const lifecycleAllowsConfiguration =
    service.lifecycle !== 'Prepared' && service.lifecycle !== 'Deleting'
  const publicReady =
    lifecycleAllowsConfiguration &&
    service.exposure.mode === 'Public' &&
    target?.hasPublicIngress &&
    (deployment.desiredOperation === 'Apply' || service.lifecycle === 'AwaitingFirstArtifact')
  const bindingsAvailable =
    bindings.isCurrent &&
    bindings.resource !== null &&
    bindings.status !== 'error' &&
    !bindings.error
  const existingHostname = bindings.resource?.find(
    (binding) =>
      binding.tenantDomainZoneId === zoneId &&
      binding.relativeName === relativeName.trim().toLowerCase(),
  )
  const draftMatches =
    existingHostname?.technicalName === technicalName.trim() ? existingHostname : undefined
  const canAttach =
    canManage &&
    publicReady &&
    zonesAvailable &&
    bindingsAvailable &&
    selected &&
    validName &&
    validTechnicalName &&
    (!existingHostname || draftMatches) &&
    relativeName.trim()
  const clear = () => {
    setZoneId('')
    setRelativeName('')
    setTechnicalName('')
    setEditing(null)
  }
  const save = () => {
    if (!canAttach || (draftMatches && draftMatches.id === editing?.id)) return
    setNotice('')
    void mutation
      .run(
        async (signal) => {
          if (!draftMatches)
            await addWebServiceHostnameBinding(
              api.baseUrl,
              service.id,
              deployment.deploymentId,
              {
                tenantDomainZoneId: zoneId,
                relativeName: relativeName.trim().toLowerCase(),
                technicalName: technicalName.trim(),
              },
              credential(),
              api.tenantId,
              signal,
            )
          if (signal.aborted || !isCurrent()) throw new DOMException('Aborted', 'AbortError')
          if (editing && bindings.resource?.some((binding) => binding.id === editing.id)) {
            try {
              await deleteWebServiceHostnameBinding(
                api.baseUrl,
                service.id,
                deployment.deploymentId,
                editing.id,
                credential(),
                api.tenantId,
                signal,
              )
            } catch (failure) {
              if (!signal.aborted && isCurrent()) {
                setNotice(
                  'The replacement alias was accepted. Detach could not be confirmed. Refresh hostnames; if the original remains, detach it to finish replacement.',
                )
                await bindings.refreshAfterWrite()
                if (signal.aborted || !isCurrent()) throw new DOMException('Aborted', 'AbortError')
                await onChanged()
              }
              throw failure
            }
          }
        },
        async (_value, guard) => {
          clear()
          setNotice(
            'Hostname configuration accepted for this target. Inspect its apply state below.',
          )
          await bindings.refreshAfterWrite()
          guard()
          await onChanged()
          guard()
          await invalidateWebServiceState(apiScope, [['domains']])
        },
      )
      .catch(() => {})
  }
  return (
    <section className="panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">Deployment target</p>
          <h3 className="panel__title">{target?.displayName ?? 'Target unavailable'}</h3>
        </div>
        <StatusBadge tone={deployment.failureCode ? 'danger' : 'neutral'}>
          {deployment.status}
        </StatusBadge>
      </div>
      <p>
        Generation {deployment.observedGeneration}/{deployment.desiredGeneration} · desired
        operation {deployment.desiredOperation}
      </p>
      {service.lifecycle === 'Prepared' && (
        <Notice>Finish the source build configuration before attaching hostnames.</Notice>
      )}
      {service.lifecycle === 'AwaitingFirstArtifact' && (
        <Notice>
          Hostname reservations are saved. Routes activate after the first successful build.
        </Notice>
      )}
      {deployment.failureCode && (
        <Notice tone="error">
          {deployment.failureCode}. Open Deployments to inspect and retry this target.
        </Notice>
      )}
      {!target?.hasPublicIngress && (
        <Notice>
          Public ingress is unavailable on this target. The platform administrator must configure
          its public base domain, ingress class and issuer.
        </Notice>
      )}
      {bindings.error && (
        <Notice tone="error">
          {bindings.error} Previously fetched hostname ownership is shown.
        </Notice>
      )}
      {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
      {notice && <Notice>{notice}</Notice>}
      <div className="list">
        {bindings.resource?.map((binding) => (
          <article className="list-row" key={binding.id}>
            <div className="list-row__primary">
              <h4>{binding.hostname}</h4>
              {binding.technicalName && <p>{binding.technicalName}</p>}
              {binding.gitOps && <p>Managed by GitOps</p>}
              <p>
                {binding.kind === 'generated'
                  ? 'Generated · maintained by this deployment'
                  : 'Custom alias'}{' '}
                · reserved {new Date(binding.createdAtUtc).toLocaleString()}
              </p>
              <a href={domainZoneHref(binding.tenantDomainZoneId, api.tenantId)}>
                Open owning zone
              </a>
            </div>
            <div className="row-actions">
              {binding.kind === 'custom' &&
                canEditDesiredConfiguration(canManage, binding.gitOps) && (
                  <>
                    <Button
                      disabled={mutation.busy || !publicReady}
                      onClick={() => {
                        setEditing(binding)
                        setZoneId(binding.tenantDomainZoneId)
                        setRelativeName(binding.relativeName)
                        setTechnicalName('')
                      }}
                    >
                      Change alias
                    </Button>
                    <Button
                      variant="danger"
                      disabled={mutation.busy || !lifecycleAllowsConfiguration}
                      onClick={() => setRemoving(binding)}
                    >
                      Detach alias
                    </Button>
                  </>
                )}
            </div>
          </article>
        ))}
      </div>
      {bindings.status === 'success' && !bindings.resource?.length && (
        <EmptyState title="No hostname reservations for this deployment." />
      )}
      <Button
        onClick={() => {
          void bindings.refresh()
          void onChanged()
        }}
      >
        Refresh hostnames
      </Button>
      {canManage && publicReady && (
        <form
          className="database-form"
          onSubmit={(event) => {
            event.preventDefault()
            save()
          }}
        >
          <h4>{editing ? `Replace ${editing.hostname}` : 'Attach a custom hostname'}</h4>
          <fieldset disabled={mutation.busy}>
            <div className="form-grid">
              <Field
                label="Binding technical name"
                hint="An immutable name unique in this tenant. Replacements require a new name."
              >
                <input
                  className="field__control"
                  required
                  maxLength={63}
                  value={technicalName}
                  pattern="[a-z]([a-z0-9-]{0,61}[a-z0-9])?"
                  onChange={(event) => setTechnicalName(event.target.value)}
                  placeholder="api-hostname"
                />
              </Field>
              <Field label="Reserved zone">
                <select
                  className="field__control"
                  required
                  value={zoneId}
                  onChange={(event) => setZoneId(event.target.value)}
                >
                  <option value="">Select a zone on this target</option>
                  {eligible.map((zone) => (
                    <option key={zone.id} value={zone.id}>
                      {zone.domain}
                    </option>
                  ))}
                </select>
              </Field>
              <Field
                label="Hostname label"
                hint="Use @ for the zone apex, or labels such as api or api.eu."
              >
                <input
                  className="field__control"
                  required
                  maxLength={200}
                  value={relativeName}
                  onChange={(event) => setRelativeName(event.target.value)}
                  placeholder="api"
                />
              </Field>
            </div>
          </fieldset>
          {selected && (
            <p className="form-message">
              Requested hostname:{' '}
              <strong>
                {relativeName.trim() === '@'
                  ? selected.domain
                  : `${relativeName.trim().toLowerCase() || 'label'}.${selected.domain}`}
              </strong>
            </p>
          )}
          {editing && (
            <p className="field__hint">
              Replacement first reserves the new alias, then detaches the original in a separate
              saved step. If detach fails, both aliases remain visible for recovery.
            </p>
          )}
          {!eligible.length && (
            <Notice>
              Reserve a tenant zone on this target before attaching a custom hostname.
            </Notice>
          )}
          <div className="row-actions">
            <Button
              type="submit"
              variant="primary"
              disabled={
                !canAttach ||
                mutation.busy ||
                (draftMatches && (!editing || draftMatches.id === editing.id))
              }
            >
              {editing ? 'Replace alias' : 'Attach alias'}
            </Button>
            <Button disabled={mutation.busy} onClick={clear}>
              Discard hostname draft
            </Button>
          </div>
        </form>
      )}
      {removing && (
        <ConfirmationDialog
          title={`Detach ${removing.hostname}?`}
          confirmLabel="Detach alias"
          busy={mutation.busy}
          onCancel={() => setRemoving(null)}
          onConfirm={() => {
            const binding = removing
            void mutation
              .run(
                (signal) =>
                  deleteWebServiceHostnameBinding(
                    api.baseUrl,
                    service.id,
                    deployment.deploymentId,
                    binding.id,
                    credential(),
                    api.tenantId,
                    signal,
                  ),
                async (_value, guard) => {
                  setRemoving(null)
                  if (editing?.id === binding.id) clear()
                  await bindings.refreshAfterWrite()
                  guard()
                  await onChanged()
                  guard()
                  await invalidateWebServiceState(apiScope, [['domains']])
                },
              )
              .catch(() => {
                if (isCurrent()) setRemoving(null)
              })
          }}
        >
          This releases this custom hostname reservation and requests removal from this target’s
          ingress. The application, zone and other targets remain.
        </ConfirmationDialog>
      )}
    </section>
  )
}
