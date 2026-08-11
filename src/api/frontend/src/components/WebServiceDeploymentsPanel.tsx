import { useEffect, useMemo, useState } from 'react'
import type {
  AvailableComputeProfile,
  AvailableDeploymentTarget,
  HostnameBinding,
  TenantDomainZone,
  WebService,
  WebServiceDeploymentInput,
  WebServiceReleaseHistory,
} from '../types'

interface WebServiceDeploymentsPanelProps {
  webService: WebService
  availableTargets: AvailableDeploymentTarget[]
  computeProfilesByTarget: Record<string, AvailableComputeProfile[]>
  hostnameBindingsByDeployment: Record<string, HostnameBinding[]>
  releases?: WebServiceReleaseHistory
  zones: TenantDomainZone[]
  canManage: boolean
  onLoadComputeProfiles: (targetId: string) => Promise<AvailableComputeProfile[]>
  onLoadHostnameBindings: (webServiceId: string, deploymentId: string) => Promise<HostnameBinding[]>
  onLoadReleases: (webServiceId: string) => Promise<WebServiceReleaseHistory | undefined>
  onAddDeployment: (id: string, input: WebServiceDeploymentInput) => Promise<unknown>
  onUpdateDeployment: (id: string, deploymentId: string, input: { fixedReplicas: number; computeProfileId?: string }) => Promise<unknown>
  onRemoveDeployment: (id: string, deploymentId: string) => Promise<unknown>
  onRetryDeployment: (id: string, deploymentId: string, expectedDeploymentRevision: number, expectedOperation?: 'Apply' | 'Delete') => Promise<unknown>
  onAddHostname: (id: string, deploymentId: string, input: { tenantDomainZoneId: string; relativeName: string }) => Promise<void>
  onRemoveHostname: (id: string, deploymentId: string, bindingId: string) => Promise<void>
}

const statusClass = (status: string) =>
  status.toLowerCase().includes('fail') || status.toLowerCase().includes('error')
    ? 'status-pill--danger'
    : status.toLowerCase().includes('pending')
      ? 'status-pill--accent'
      : 'status-pill--success'

export const retryOperationForStatus = (status: string): 'Apply' | 'Delete' =>
  status.toLowerCase().includes('delete') ? 'Delete' : 'Apply'

export function WebServiceDeploymentsPanel({
  webService,
  availableTargets,
  computeProfilesByTarget,
  hostnameBindingsByDeployment,
  releases,
  zones,
  canManage,
  onLoadComputeProfiles,
  onLoadHostnameBindings,
  onLoadReleases,
  onAddDeployment,
  onUpdateDeployment,
  onRemoveDeployment,
  onRetryDeployment,
  onAddHostname,
  onRemoveHostname,
}: WebServiceDeploymentsPanelProps) {
  const [addTargetId, setAddTargetId] = useState('')
  const [addReplicas, setAddReplicas] = useState('1')
  const [addComputeProfileId, setAddComputeProfileId] = useState('')
  const [aliasDrafts, setAliasDrafts] = useState<Record<string, { zoneId: string; relativeName: string }>>({})

  useEffect(() => {
    void onLoadReleases(webService.id)
    webService.deployments.forEach((deployment) => {
      void onLoadHostnameBindings(webService.id, deployment.deploymentId)
      void onLoadComputeProfiles(deployment.deploymentTargetId)
    })
  }, [onLoadComputeProfiles, onLoadHostnameBindings, onLoadReleases, webService.deployments, webService.id])

  const unusedTargets = useMemo(
    () => availableTargets.filter((target) => !webService.deployments.some(
      (deployment) => deployment.deploymentTargetId === target.id,
    )),
    [availableTargets, webService.deployments],
  )

  const addProfiles = computeProfilesByTarget[addTargetId] ?? []
  const targetZones = (targetId: string) => zones.filter((zone) =>
    zone.deploymentTargetId === targetId && zone.kind === 'managed-reservation',
  )

  const addTarget = async () => {
    if (!addTargetId) return
    await onAddDeployment(webService.id, {
      deploymentTargetId: addTargetId,
      fixedReplicas: Number(addReplicas),
      computeProfileId: addComputeProfileId || undefined,
    })
    setAddTargetId('')
    setAddComputeProfileId('')
    setAddReplicas('1')
  }

  return (
    <section className="panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">Placement</p>
          <h2 className="panel__title">Physical deployments</h2>
        </div>
        <span className="panel__meta">{webService.deployments.length} active</span>
      </div>

      <p className="form-message">Each target reconciles independently. A failed target does not block the other deployments in this release.</p>

      <div className="list">
        {webService.deployments.map((deployment) => {
          const bindings = hostnameBindingsByDeployment[deployment.deploymentId] ?? []
          const profiles = computeProfilesByTarget[deployment.deploymentTargetId] ?? []
          const draft = aliasDrafts[deployment.deploymentId] ?? { zoneId: '', relativeName: '' }
          const matchingZones = targetZones(deployment.deploymentTargetId)
          return (
            <article className="list-row" key={deployment.deploymentId}>
              <div className="list-row__primary">
                <h3>{availableTargets.find((target) => target.id === deployment.deploymentTargetId)?.displayName ?? deployment.deploymentTargetId}</h3>
                <p>Generation {deployment.observedGeneration}/{deployment.desiredGeneration} · {deployment.fixedReplicas} fixed replicas</p>
                {deployment.failureCode && <p className="form-message form-message--error">{deployment.failureCode}</p>}
              </div>
              <div className="list-row__secondary">
                <span className={`status-pill ${statusClass(deployment.status)}`}>{deployment.status}</span>
                <span className="list-row__meta">{deployment.compute.displayName}</span>
              </div>

              <div className="form-grid">
                <label className="field"><span className="field__label">Fixed replicas</span><input className="field__control" disabled={!canManage} type="number" min="1" max="1000" defaultValue={deployment.fixedReplicas} onBlur={(event) => { const fixedReplicas = Number(event.target.value); if (fixedReplicas !== deployment.fixedReplicas && fixedReplicas > 0) void onUpdateDeployment(webService.id, deployment.deploymentId, { fixedReplicas, computeProfileId: deployment.compute.profile.computeProfileId }) }} /></label>
                <label className="field"><span className="field__label">Compute profile</span><select className="field__control" disabled={!canManage} defaultValue={deployment.compute.profile.computeProfileId} onChange={(event) => void onUpdateDeployment(webService.id, deployment.deploymentId, { fixedReplicas: deployment.fixedReplicas, computeProfileId: event.target.value || undefined })}><option value="">Target default</option>{profiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.displayName}</option>)}</select></label>
              </div>

              <div className="form-message">
                <strong>Hostnames</strong>
                {bindings.length === 0 && <p>{deployment.ingress?.hostnames.join(', ') || 'No ingress hostnames reported yet.'}</p>}
                {bindings.map((binding) => <p key={binding.id}>{binding.hostname} {binding.kind === 'generated' ? <span className="field__hint">generated</span> : <button className="button button--danger" disabled={!canManage} type="button" onClick={() => void onRemoveHostname(webService.id, deployment.deploymentId, binding.id)}>Remove alias</button>}</p>)}
                {canManage && matchingZones.length > 0 && <div className="form-grid"><select className="field__control" value={draft.zoneId} onChange={(event) => setAliasDrafts((current) => ({ ...current, [deployment.deploymentId]: { ...draft, zoneId: event.target.value } }))}><option value="">Select a reserved zone</option>{matchingZones.map((zone) => <option key={zone.id} value={zone.id}>{zone.domain}</option>)}</select><input className="field__control" value={draft.relativeName} onChange={(event) => setAliasDrafts((current) => ({ ...current, [deployment.deploymentId]: { ...draft, relativeName: event.target.value } }))} placeholder="api" /><button className="button" type="button" disabled={!draft.zoneId || !draft.relativeName.trim()} onClick={() => void onAddHostname(webService.id, deployment.deploymentId, { tenantDomainZoneId: draft.zoneId, relativeName: draft.relativeName.trim() })}>Add alias</button></div>}
              </div>

              <div className="row-actions">
                <button className="button" disabled={!canManage} type="button" onClick={() => void onRetryDeployment(webService.id, deployment.deploymentId, deployment.deploymentRevision, retryOperationForStatus(deployment.status))}>Retry</button>
                <button className="button button--danger" disabled={!canManage || webService.deployments.length <= 1} type="button" onClick={() => { if (window.confirm('Remove this physical deployment? The last deployment cannot be removed.')) void onRemoveDeployment(webService.id, deployment.deploymentId) }}>Remove target</button>
              </div>
            </article>
          )
        })}
      </div>

      {canManage && unusedTargets.length > 0 && <div className="form-grid"><select className="field__control" value={addTargetId} onChange={(event) => { setAddTargetId(event.target.value); setAddComputeProfileId(''); if (event.target.value) void onLoadComputeProfiles(event.target.value) }}><option value="">Add an eligible target</option>{unusedTargets.map((target) => <option key={target.id} value={target.id}>{target.displayName}</option>)}</select><input className="field__control" type="number" min="1" max="1000" value={addReplicas} onChange={(event) => setAddReplicas(event.target.value)} /><select className="field__control" value={addComputeProfileId} onChange={(event) => setAddComputeProfileId(event.target.value)}><option value="">Target default compute</option>{addProfiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.displayName}</option>)}</select><button className="button button--primary" type="button" disabled={!addTargetId} onClick={() => void addTarget()}>Add target</button></div>}

      <div className="form-message">
        <strong>Releases</strong>
        {!releases && <p>Loading release history…</p>}
        {releases?.releases.items.map((release) => <p key={release.id}>Revision {release.resourceRevision}: {release.action} · {release.outcome} ({release.deployments.length} deployment entries)</p>)}
      </div>
    </section>
  )
}
