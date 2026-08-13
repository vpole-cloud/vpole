import { useState } from 'react'
import type { AvailableDeploymentTarget, LoadStatus, TenantDomainZone } from '../types'

interface DomainZonesPageProps {
  items: TenantDomainZone[]
  targets: AvailableDeploymentTarget[]
  status: LoadStatus
  error?: string
  submitStatus: LoadStatus
  submitError?: string
  onRefresh: () => Promise<void>
  onCreate: (input: { deploymentTargetId: string; label: string }) => Promise<void>
  onDelete: (id: string) => Promise<void>
  canManage: boolean
}

export function DomainZonesPage({
  items,
  targets,
  status,
  error,
  submitStatus,
  submitError,
  onRefresh,
  onCreate,
  onDelete,
  canManage,
}: DomainZonesPageProps) {
  const [deploymentTargetId, setDeploymentTargetId] = useState('')
  const [label, setLabel] = useState('')

  const reserve = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!deploymentTargetId || !label.trim()) return
    await onCreate({ deploymentTargetId, label: label.trim() })
    setLabel('')
  }

  return (
    <div className="resource-groups-page">
      <section className="hero-panel">
        <div className="hero-panel__main">
          <p className="hero-panel__eyebrow">Networking</p>
          <h1 className="hero-panel__title">Domain zones</h1>
          <p className="hero-panel__description">Reserve tenant-owned subdomains under a target’s public base domain. Use these zones for WebService hostname aliases.</p>
        </div>
        <aside className="hero-panel__utility"><button className="button" type="button" onClick={() => void onRefresh()}>Refresh</button></aside>
      </section>

      <div className="resource-groups-layout resource-groups-layout--wide-form">
        <section className="panel resource-groups-list-panel">
          <div className="panel__header"><div><p className="panel__eyebrow">Owned zones</p><h2 className="panel__title">Reservations</h2></div><span className="panel__meta">{items.length} visible</span></div>
          {status === 'loading' && <div className="empty-state">Loading domain zones…</div>}
          {status === 'error' && <div className="empty-state empty-state--error">{error || 'Unable to load domain zones.'}</div>}
          {status === 'success' && items.length === 0 && <div className="empty-state">No custom zone is reserved yet. Generated deployment hostnames do not need a reservation.</div>}
          {status === 'success' && items.length > 0 && <div className="list">{items.map((zone) => <article className="list-row" key={zone.id}><div className="list-row__primary"><h3>{zone.domain}</h3><p>{zone.kind === 'generated' ? 'Generated zone' : 'Managed reservation'}</p></div><div className="row-actions">{zone.kind === 'managed-reservation' && <button className="button button--danger" disabled={!canManage} type="button" onClick={() => void onDelete(zone.id)}>Remove</button>}</div></article>)}</div>}
        </section>

        <section className="panel web-service-form-panel">
          <div className="panel__header"><div><p className="panel__eyebrow">Reserve</p><h2 className="panel__title">Managed child zone</h2></div></div>
          {canManage ? <form className="resource-group-form" onSubmit={(event) => void reserve(event)}><label className="field"><span className="field__label">Deployment target</span><select className="field__control" value={deploymentTargetId} onChange={(event) => setDeploymentTargetId(event.target.value)}><option value="">Select target</option>{targets.map((target) => <option key={target.id} value={target.id}>{target.displayName} · {target.publicBaseDomain}</option>)}</select></label><label className="field"><span className="field__label">Zone label</span><input className="field__control" value={label} onChange={(event) => setLabel(event.target.value)} placeholder="company" /><span className="field__hint">Creates `company` under the selected target’s public base domain.</span></label>{submitError && <div className="form-message form-message--error">{submitError}</div>}<div className="resource-group-form__actions"><button className="button button--primary" type="submit" disabled={!deploymentTargetId || !label.trim() || submitStatus === 'loading'}>{submitStatus === 'loading' ? 'Reserving…' : 'Reserve zone'}</button></div></form> : <div className="empty-state">Tenant viewer access is read-only.</div>}
        </section>
      </div>
    </div>
  )
}
