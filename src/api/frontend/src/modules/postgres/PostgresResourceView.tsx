import { useEffect, useState } from 'react'
import { deletePostgresCluster, fetchAvailableComputeProfiles, fetchAvailableDeploymentTargets, fetchAvailableStorageProfiles, fetchIsolationScopes, fetchPostgresCluster, fetchResourceGroup, retryResourceDeployment, updatePostgresCluster, validationMessage } from '../../api'
import { useResource } from '../../hooks/useResource'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import type { PostgresCluster } from '../../types'
import { Button, ConfirmationDialog, EmptyState, Field, Notice, StatusBadge } from '../../ui/components'
import { PlacementSummary } from '../../ui/PlacementSummary'
import { ResourceEndpoints } from '../../ui/ResourceEndpoints'
import { Tabs, TabPanel } from '../../ui/Tabs'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import type { PostgresSection } from './workspaceState'
import { PostgresCredentials } from './PostgresCredentials'

const resourceType = 'pole/postgresql'
export function PostgresResourceView({ id, section, active, version, onSection, actions }:
  { id: string; section: PostgresSection; active: boolean; version: number; onSection: (section: PostgresSection) => void; actions: WorkspaceActions<PostgresSection> }) {
  const { api, tenant, navigation } = useAppServices()
  const scopeKey = `${api.baseUrl}:${api.tenantId}:${api.token}:${id}`
  const details = useResource(scopeKey, (signal) => fetchPostgresCluster(api.baseUrl, id, api.token, api.tenantId, signal), version)
  const cluster = details.resource
  const group = useResource(`${scopeKey}:${cluster?.groupId}`, (signal) => cluster ? fetchResourceGroup(api.baseUrl, cluster.groupId, api.token, api.tenantId, signal) : Promise.resolve(null))
  const scopes = useResource(`${scopeKey}:${cluster?.groupId}:scopes`, (signal) => cluster ? fetchIsolationScopes(api.baseUrl, cluster.groupId, api.token, api.tenantId, signal) : Promise.resolve([]))
  const targets = useResource(`${scopeKey}:${cluster?.placement.isolationScopeId}`, (signal) => cluster ? fetchAvailableDeploymentTargets(api.baseUrl, cluster.placement.isolationScopeId ?? undefined, api.token, api.tenantId, signal, false, resourceType) : Promise.resolve([]))
  const storage = useResource(`${scopeKey}:${cluster?.placement.deploymentTargetId}:storage`, (signal) => cluster ? fetchAvailableStorageProfiles(api.baseUrl, resourceType, cluster.placement.deploymentTargetId, api.token, api.tenantId, signal) : Promise.resolve([]))
  const mutation = useScopedMutation(scopeKey)
  const [deleting, setDeleting] = useState(false)
  const [visited, setVisited] = useState(section === 'configuration')
  const onLoaded = actions.loaded
  const onDirty = actions.dirty
  const [configurationDirty, setConfigurationDirty] = useState(false)
  const [credentialsDirty, setCredentialsDirty] = useState(false)
  const dirty = configurationDirty || credentialsDirty
  const refresh = details.refresh
  if (section === 'configuration' && !visited) setVisited(true)
  useEffect(() => { if (cluster) onLoaded(cluster.id, cluster.name) }, [cluster, onLoaded])
  useEffect(() => { onDirty(id, dirty); return () => onDirty(id, false) }, [dirty, id, onDirty])
  const reconciling = cluster && (cluster.lifecycle === 'Deleting' || ['WaitingForRuntimeCell', 'Pending', 'Reconciling', 'Deleting', 'Failed'].includes(cluster.placement.status))
  useEffect(() => {
    if (!active || !reconciling) return
    const timer = window.setInterval(() => void refresh(), 2500)
    return () => window.clearInterval(timer)
  }, [active, reconciling, refresh])
  if (!cluster) return <EmptyState title={details.status === 'error' ? 'Unable to open this database.' : 'Loading database…'} error={details.status === 'error'}>
    {details.error && <p>{details.error}</p>}<Button onClick={() => void details.refresh()}>Retry database</Button></EmptyState>
  const panelId = `postgres-${id}`
  const canWrite = tenant.canManage && cluster.lifecycle === 'Active'
  return <div><header className="ui-page-header"><div><p className="ui-page-eyebrow">PostgreSQL · {group.resource?.name ?? 'Resource group'}</p><h1>{cluster.name}</h1>
    <p>PostgreSQL {cluster.spec.version} · {cluster.spec.deploymentMode === 'Operator' ? 'CloudNativePG' : 'Native StatefulSet'}</p></div>
    <div className="row-actions"><Button onClick={() => void details.refresh()} disabled={details.status === 'loading'}>Refresh database</Button>
      {canWrite && <Button variant="danger" onClick={() => setDeleting(true)}>Delete database</Button>}</div></header>
    {details.error && <Notice tone="error">{details.error} Last fetched metadata is shown.</Notice>}
    {cluster.lifecycle === 'Deleting' && <Notice>This database is being deleted. Wait for cleanup to complete.</Notice>}
    <Tabs id={panelId} label={`${cluster.name} sections`} variant="section" activeId={section} onSelect={(section) => onSection(section as PostgresSection)}
      tabs={[{ id: 'overview', label: 'Overview' }, { id: 'configuration', label: 'Configuration' }, ...(tenant.canManage ? [{ id: 'credentials', label: 'Credentials' }] : [])]} />
    <TabPanel id={panelId} tabId="overview" activeId={section}><section className="panel"><div className="panel__header"><h2 className="panel__title">Database overview</h2><StatusBadge>{cluster.lifecycle}</StatusBadge></div>
      <dl className="web-service-facts"><div><dt>Resource group</dt><dd><a href={navigation.resourceHref({ resourceTypeId: 'pole/resource-group', resourceId: cluster.groupId })}>{group.resource?.name ?? cluster.groupId}</a></dd></div>
        <div><dt>Isolation scope</dt><dd>{scopes.resource?.find((scope) => scope.id === cluster.placement.isolationScopeId)?.name ?? cluster.placement.isolationScopeId}</dd></div>
        <div><dt>Instances</dt><dd>{cluster.spec.instances}</dd></div><div><dt>Storage per instance</dt><dd>{cluster.spec.storage} GiB</dd></div>
        <div><dt>Storage profile</dt><dd>{storage.resource?.find((profile) => profile.id === cluster.spec.storageProfileId)?.displayName ?? cluster.spec.storageProfileId}</dd></div>
        <div><dt>Revision</dt><dd>{cluster.revision}</dd></div><div><dt>Group tags</dt><dd>{group.resource?.tags.join(', ') || 'No tags'}</dd></div></dl>
      <p className="field__hint">This database has one deployment target. Network access and application credentials are configured separately.</p></section>
      <PlacementSummary placement={cluster.placement} targetName={targets.resource?.find((target) => target.id === cluster.placement.deploymentTargetId)?.displayName} />
      {active && section === 'overview' && <ResourceEndpoints resourceType="pole/postgresql" resourceId={id} />}
      {tenant.canManage && cluster.placement.status === 'Failed' && <Button disabled={mutation.busy} onClick={() => {
        void mutation.run((signal) => retryResourceDeployment(api.baseUrl, cluster.placement.deploymentId, api.token, api.tenantId, signal), async () => { await details.refresh(); actions.changed() }).catch(() => {})
      }}>Retry deployment</Button>}
    </TabPanel>
    <TabPanel id={panelId} tabId="configuration" activeId={section}>{visited && (canWrite ? <PostgresConfiguration cluster={cluster} onDirtyChange={setConfigurationDirty} onSaved={async () => { await details.refresh(); actions.changed() }} /> : <EmptyState title="Database configuration is read-only." />)}</TabPanel>
    <TabPanel id={panelId} tabId="credentials" activeId={section}>
      {active && section === 'credentials' && canWrite && <PostgresCredentials key={`${scopeKey}:${tenant.canManage}`} cluster={cluster} onDirtyChange={setCredentialsDirty} onChanged={async () => { await details.refresh(); actions.changed() }} />}
      {!tenant.canManage && section === 'credentials' && <EmptyState title="Tenant viewer access is read-only. Credentials require write access." />}
    </TabPanel>
    {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    {deleting && canWrite && <ConfirmationDialog title={`Delete ${cluster.name}?`} confirmLabel="Delete database" busy={mutation.busy} onCancel={() => setDeleting(false)} onConfirm={() => {
      if (!canWrite) return
      void mutation.run((signal) => deletePostgresCluster(api.baseUrl, id, api.token, api.tenantId, signal), async () => {
        setDeleting(false); await details.refresh(); actions.changed()
      }).catch(() => {})
    }}>This removes the database workload and its storage. Applications using it will lose access.</ConfirmationDialog>}
  </div>
}

function PostgresConfiguration({ cluster, onDirtyChange, onSaved }: { cluster: PostgresCluster; onDirtyChange: (dirty: boolean) => void; onSaved: () => Promise<void> }) {
  const { api, notifications } = useAppServices()
  const scopeKey = `${api.baseUrl}:${api.tenantId}:${api.token}:${cluster.id}`
  const compute = useResource(`${scopeKey}:${cluster.placement.deploymentTargetId}`, (signal) => fetchAvailableComputeProfiles(api.baseUrl, cluster.placement.deploymentTargetId, api.token, api.tenantId, signal, resourceType))
  const [instances, setInstances] = useState(cluster.spec.instances)
  const [storage, setStorage] = useState(cluster.spec.storage)
  const [computeId, setComputeId] = useState('')
  const snapshot = JSON.stringify([instances, storage, computeId])
  const [saved, setSaved] = useState(snapshot)
  const [loadedRevision, setLoadedRevision] = useState(cluster.revision)
  const dirty = saved !== snapshot
  if (!dirty && loadedRevision !== cluster.revision) {
    setLoadedRevision(cluster.revision); setInstances(cluster.spec.instances); setStorage(cluster.spec.storage); setComputeId('')
    setSaved(JSON.stringify([cluster.spec.instances, cluster.spec.storage, '']))
  }
  useUnsavedChanges(dirty, cluster.name)
  useEffect(() => { onDirtyChange(dirty); return () => onDirtyChange(false) }, [onDirtyChange, dirty])
  const mutation = useScopedMutation(scopeKey)
  const [discarding, setDiscarding] = useState(false)
  const [validation, setValidation] = useState('')
  const fieldError = (field: string) => validationMessage(mutation.error, field)
  const native = cluster.spec.deploymentMode === 'StatefulSet'
  return <section className="panel"><h2 className="panel__title">Database configuration</h2><p className="field__hint">Name, PostgreSQL version, group, target and storage profile are fixed after creation. {native ? 'Native StatefulSet storage is fixed; compute can be updated.' : 'Operator-managed storage can grow; shrinking is rejected.'}</p>
    <form className="database-form" onSubmit={(event) => {
      event.preventDefault(); if (mutation.busy) return
      if (!Number.isInteger(instances) || instances < 1 || !Number.isInteger(storage) || storage < cluster.spec.storage || (computeId && (compute.status !== 'success' || !compute.resource?.some((profile) => profile.id === computeId)))) { setValidation('Choose valid capacity and an available compute profile. Storage cannot shrink.'); return }
      setValidation('')
      void mutation.run((signal) => updatePostgresCluster(api.baseUrl, cluster.id, { instances, storage, computeProfileId: computeId || undefined }, api.token, api.tenantId, signal), async () => {
        setSaved(snapshot); await onSaved(); notifications.publish(`Configuration accepted for ${cluster.name}.`)
      }).catch(() => {})
    }}><fieldset disabled={mutation.busy}><div className="form-grid">
      <Field label="Instances"><input className="field__control" type="number" min="1" step="1" value={instances} disabled={cluster.spec.deploymentMode === 'StatefulSet'} onChange={(event) => setInstances(Number(event.target.value))} />{fieldError('Instances') && <span className="field-error">{fieldError('Instances')}</span>}</Field>
      <Field label="Storage per instance (GiB)"><input className="field__control" type="number" min={cluster.spec.storage} step="1" value={storage} disabled={native} onChange={(event) => setStorage(Number(event.target.value))} />{fieldError('Storage') && <span className="field-error">{fieldError('Storage')}</span>}</Field>
      <Field label="Compute profile" hint="Keeping the current profile preserves its resolved resource snapshot."><select className="field__control" value={computeId} onChange={(event) => setComputeId(event.target.value)}>
        <option value="">Keep current · {cluster.placement.compute.displayName}</option>{compute.resource?.map((profile) => <option key={profile.id} value={profile.id}>{profile.displayName}</option>)}</select>{fieldError('ComputeProfileId') && <span className="field-error">{fieldError('ComputeProfileId')}</span>}</Field>
    </div></fieldset>
    {compute.error && <Notice tone="error">{compute.error}<Button onClick={() => void compute.refresh()}>Retry compute profiles</Button></Notice>}
    {validation && <Notice tone="error">{validation}</Notice>}{mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    <div className="row-actions"><Button variant="primary" type="submit" disabled={mutation.busy || !dirty}>{mutation.busy ? 'Saving…' : 'Save changes'}</Button><Button disabled={mutation.busy} onClick={() => setDiscarding(true)}>Discard changes</Button></div>
    </form>
    {discarding && <ConfirmationDialog title="Discard database changes?" confirmLabel="Discard changes" onCancel={() => setDiscarding(false)} onConfirm={() => {
      setInstances(cluster.spec.instances); setStorage(cluster.spec.storage); setComputeId(''); setSaved(JSON.stringify([cluster.spec.instances, cluster.spec.storage, ''])); mutation.clearError(); setDiscarding(false)
    }}>The current server configuration will replace your unsaved draft.</ConfirmationDialog>}
  </section>
}
