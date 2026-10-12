import { eligibilityKeys } from '../../data/queryKeys'
import { ResourceHealth } from '../../ui/ResourceHealth'
import { ResourceOperations } from '../../ui/ResourceOperations'
import { useEffect, useState } from 'react'
import { ApiError, deletePostgresCluster, fetchAvailableComputeProfiles, fetchAvailableDeploymentTargets, fetchAvailableStorageProfiles, fetchIsolationScopes, fetchPostgresCluster, fetchResourceGroup, retryResourceDeployment, updatePostgresCluster, validationMessage } from '../../api'
import { useScopedQuery } from '../../data/useScopedQuery'
import { groupInterests, resourceInterests } from '../../realtime/resourceInterests'
import { useVisibleRefresh } from '../../hooks/useVisibleRefresh'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
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
import { acceptedPostgresChanges, isPostgresDirty, postgresChanges, postgresConfiguration, reapplyPostgresChanges } from './configurationModel'

const resourceType = 'pole/postgresql'
export function PostgresResourceView({ id, section, active, onSection, actions }:
  { id: string; section: PostgresSection; active: boolean; onSection: (section: PostgresSection) => void; actions: WorkspaceActions<PostgresSection> }) {
  const { api, tenant, navigation } = useAppServices()
  const origin = useProviderInvalidation(api)
  const scopeKey = `${origin.identity}:${id}:${tenant.canManage}`
  const scope = [api.baseUrl, api.tenantId]
  const realtime = resourceInterests('postgres', api.tenantId, id)
  const details = useScopedQuery<PostgresCluster>({ scope, key: ['postgres', 'detail', id], active, realtime, resourceGroupId: (value) => value.groupId,
    load: (signal) => fetchPostgresCluster(api.baseUrl, id, origin.credential(), api.tenantId, signal) })
  const cluster = details.resource
  const interest = { active, realtime: groupInterests(api.tenantId, cluster?.groupId), resourceGroupId: cluster?.groupId }
  const group = useScopedQuery({ scope, key: ['resource-groups', 'detail', cluster?.groupId], enabled: Boolean(cluster), ...interest, load: (signal) => fetchResourceGroup(api.baseUrl, cluster!.groupId, origin.credential(), api.tenantId, signal) })
  const scopes = useScopedQuery({ scope, key: eligibilityKeys.scopes(cluster?.groupId), enabled: Boolean(cluster), ...interest, load: (signal) => fetchIsolationScopes(api.baseUrl, cluster!.groupId, origin.credential(), api.tenantId, signal) })
  const targets = useScopedQuery({ scope, key: eligibilityKeys.targets(resourceType, cluster?.placement.isolationScopeId, false), enabled: Boolean(cluster), ...interest, load: (signal) => fetchAvailableDeploymentTargets(api.baseUrl, cluster!.placement.isolationScopeId ?? undefined, origin.credential(), api.tenantId, signal, false, resourceType) })
  const storage = useScopedQuery({ scope, key: eligibilityKeys.storage(resourceType, cluster?.placement.deploymentTargetId), enabled: Boolean(cluster), ...interest, load: (signal) => fetchAvailableStorageProfiles(api.baseUrl, resourceType, cluster!.placement.deploymentTargetId, origin.credential(), api.tenantId, signal) })
  const mutation = useScopedMutation(scopeKey)
  const [deleting, setDeleting] = useState(false)
  const [visited, setVisited] = useState(section === 'configuration')
  const onLoaded = actions.loaded
  const onDirty = actions.dirty
  const [configurationDirty, setConfigurationDirty] = useState(false)
  const [credentialsDirty, setCredentialsDirty] = useState(false)
  const dirty = configurationDirty || credentialsDirty
  if (section === 'configuration' && !visited) setVisited(true)
  useEffect(() => { if (cluster) onLoaded(cluster.id, cluster.name) }, [cluster, onLoaded])
  useEffect(() => { onDirty(id, dirty); return () => onDirty(id, false) }, [dirty, id, onDirty])
  const reconciling = Boolean(cluster && (cluster.lifecycle === 'Deleting' || ['WaitingForRuntimeCell', 'Pending', 'Reconciling', 'Deleting', 'Failed'].includes(cluster.placement.status)))
  useVisibleRefresh(active && reconciling, details.refresh, 2500, realtime)
  const afterWrite = async () => {
    await Promise.all([details.refreshAfterWrite(), origin.invalidate([['postgres', 'inventory'], ['resource-groups'], ['overview'], ['search'], ['operations'], ['health'], ['endpoints']])])

  }
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
      tabs={[{ id: 'overview', label: 'Overview' }, { id: 'operations', label: 'Operations' }, { id: 'configuration', label: 'Configuration' }, ...(tenant.canManage ? [{ id: 'credentials', label: 'Credentials' }] : [])]} />
    <TabPanel id={panelId} tabId="overview" activeId={section}><section className="panel"><div className="panel__header"><h2 className="panel__title">Database overview</h2><StatusBadge>{cluster.lifecycle}</StatusBadge></div>
      <dl className="web-service-facts"><div><dt>Resource group</dt><dd><a href={navigation.resourceHref({ resourceTypeId: 'pole/resource-group', resourceId: cluster.groupId })}>{group.resource?.name ?? cluster.groupId}</a></dd></div>
        <div><dt>Isolation scope</dt><dd>{scopes.resource?.find((scope) => scope.id === cluster.placement.isolationScopeId)?.name ?? cluster.placement.isolationScopeId}</dd></div>
        <div><dt>Instances</dt><dd>{cluster.spec.instances}</dd></div><div><dt>Storage per instance</dt><dd>{cluster.spec.storage} GiB</dd></div>
        <div><dt>Storage profile</dt><dd>{storage.resource?.find((profile) => profile.id === cluster.spec.storageProfileId)?.displayName ?? cluster.spec.storageProfileId}</dd></div>
        <div><dt>Revision</dt><dd>{cluster.revision}</dd></div><div><dt>Group tags</dt><dd>{group.resource?.tags.join(', ') || 'No tags'}</dd></div></dl>
      <p className="field__hint">This database has one deployment target. Network access and application credentials are configured separately.</p></section>
      <PlacementSummary placement={cluster.placement} targetName={targets.resource?.find((target) => target.id === cluster.placement.deploymentTargetId)?.displayName} />
      <ResourceHealth placements={[cluster.placement]} active={active && section === 'overview'} targetNames={Object.fromEntries((targets.resource ?? []).map((target) => [target.id, target.displayName]))} />
      {active && section === 'overview' && <ResourceEndpoints resourceType="pole/postgresql" resourceId={id} />}
      {tenant.canManage && cluster.placement.status === 'Failed' && <Button disabled={mutation.busy} onClick={() => {
        void mutation.run((signal) => retryResourceDeployment(api.baseUrl, cluster.placement.deploymentId, cluster.placement.deploymentRevision ?? 1, cluster.placement.desiredOperation ?? "Apply", origin.credential(), api.tenantId, signal), afterWrite).catch(() => {})
      }}>Retry deployment</Button>}
    </TabPanel>
    <TabPanel id={panelId} tabId="operations" activeId={section}>{active && section === "operations" && <ResourceOperations filter={{ resourceType: "pole/postgresql", resourceId: id }} onAccepted={afterWrite} onRetry={(operation, signal) => retryResourceDeployment(api.baseUrl, operation.deployment.id, operation.deployment.placement.deploymentRevision!, operation.deployment.placement.desiredOperation!, origin.credential(), api.tenantId, signal)} />}</TabPanel>
    <TabPanel id={panelId} tabId="configuration" activeId={section}>{visited && (canWrite ? <PostgresConfiguration cluster={cluster} active={active && section === 'configuration'} onDirtyChange={setConfigurationDirty} onRefresh={details.refresh} onSaved={afterWrite} /> : <EmptyState title="Database configuration is read-only." />)}</TabPanel>
    <TabPanel id={panelId} tabId="credentials" activeId={section}>
      {active && section === 'credentials' && canWrite && <PostgresCredentials key={`${scopeKey}:${tenant.canManage}`} cluster={cluster} onDirtyChange={setCredentialsDirty} onChanged={afterWrite} />}
      {!tenant.canManage && section === 'credentials' && <EmptyState title="Tenant viewer access is read-only. Credentials require write access." />}
    </TabPanel>
    {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    {deleting && canWrite && <ConfirmationDialog title={`Delete ${cluster.name}?`} confirmLabel="Delete database" busy={mutation.busy} onCancel={() => setDeleting(false)} onConfirm={() => {
      if (!canWrite) return
      void mutation.run((signal) => deletePostgresCluster(api.baseUrl, id, origin.credential(), api.tenantId, signal), async () => {
        setDeleting(false); await afterWrite()
      }).catch(() => {})
    }}>This removes the database workload and its storage. Applications using it will lose access.</ConfirmationDialog>}
  </div>
}

function PostgresConfiguration({ cluster, active, onDirtyChange, onSaved, onRefresh }: { cluster: PostgresCluster; active: boolean; onDirtyChange: (dirty: boolean) => void; onSaved: () => Promise<void>; onRefresh: () => Promise<PostgresCluster | null> }) {
  const { api, notifications } = useAppServices()
  const origin = useProviderInvalidation(api)
  const scopeKey = `${origin.identity}:${cluster.id}`
  const compute = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: eligibilityKeys.compute(resourceType, cluster.placement.deploymentTargetId), active,
    realtime: groupInterests(api.tenantId, cluster.groupId), resourceGroupId: cluster.groupId,
    load: (signal) => fetchAvailableComputeProfiles(api.baseUrl, cluster.placement.deploymentTargetId, origin.credential(), api.tenantId, signal, resourceType) })
  const [configuration, setConfiguration] = useState(() => postgresConfiguration(cluster))
  const { instances, storage, computeProfileId } = configuration.draft
  const dirty = isPostgresDirty(configuration)
  if (!dirty && configuration.baseline.revision !== cluster.revision) setConfiguration(postgresConfiguration(cluster))
  useUnsavedChanges(dirty, cluster.name)
  useEffect(() => { onDirtyChange(dirty); return () => onDirtyChange(false) }, [onDirtyChange, dirty])
  const mutation = useScopedMutation(scopeKey)
  const [discarding, setDiscarding] = useState(false)
  const [reapplying, setReapplying] = useState(false)
  const [validation, setValidation] = useState('')
  const fieldError = (field: string) => validationMessage(mutation.error, field)
  const conflict = dirty && (configuration.baseline.revision !== cluster.revision || (mutation.error instanceof ApiError && mutation.error.code === 'postgres.update.concurrent-change'))
  const awaitingRefresh = configuration.accepted && configuration.baseline.revision === cluster.revision
  const native = cluster.spec.deploymentMode === 'StatefulSet'
  const change = (field: 'instances' | 'storage' | 'computeProfileId', value: number | string) =>
    setConfiguration((current) => ({ ...current, draft: { ...current.draft, [field]: value } }))
  const changes = postgresChanges(configuration)
  return <section className="panel"><h2 className="panel__title">Database configuration</h2><p className="field__hint">Name, PostgreSQL version, group, target and storage profile are fixed after creation. {native ? 'Native StatefulSet storage is fixed; compute can be updated.' : 'Operator-managed storage can grow; shrinking is rejected.'}</p>
    {conflict && <Notice tone="error">The server configuration changed since revision {configuration.baseline.revision}. Your draft is preserved. Review the current configuration before saving.
      <dl className="web-service-facts"><div><dt>Current server revision</dt><dd>{cluster.revision}</dd></div>
        <div><dt>Instances: server / draft</dt><dd>{cluster.spec.instances} / {instances}</dd></div>
        <div><dt>Storage GiB: server / draft</dt><dd>{cluster.spec.storage} / {storage}</dd></div>
        <div><dt>Compute profile: server / draft</dt><dd>{cluster.placement.compute.displayName} / {compute.resource?.find((profile) => profile.id === computeProfileId)?.displayName ?? (computeProfileId || 'Keep current')}</dd></div></dl>
      <Button disabled={mutation.busy} onClick={() => setReapplying(true)}>Review and reapply draft</Button>
      <Button disabled={mutation.busy} onClick={() => void onRefresh()}>Refresh current configuration</Button>
    </Notice>}
    {awaitingRefresh && <Notice>Configuration was accepted. Refresh the database to load its new revision.</Notice>}
    <form className="database-form" onSubmit={(event) => {
      event.preventDefault(); if (mutation.busy || !dirty || conflict || awaitingRefresh) return
      if (!Number.isInteger(instances) || instances < 1 || !Number.isInteger(storage) || storage < cluster.spec.storage || (computeProfileId && (compute.status !== 'success' || !compute.resource?.some((profile) => profile.id === computeProfileId)))) { setValidation('Choose valid capacity and an available compute profile. Storage cannot shrink.'); return }
      setValidation('')
      const submitted = configuration
      void mutation.run((signal) => updatePostgresCluster(api.baseUrl, cluster.id, postgresChanges(submitted), origin.credential(), api.tenantId, signal), async () => {
        setConfiguration(acceptedPostgresChanges(submitted)); await onSaved(); if (!origin.isCurrent()) return; notifications.publish(`Configuration accepted for ${cluster.name}.`)
      }).catch((error: unknown) => {
        if (origin.isCurrent() && error instanceof ApiError && error.code === 'postgres.update.concurrent-change') void onRefresh()
      })
    }}><fieldset disabled={mutation.busy || awaitingRefresh}><div className="form-grid">
      <Field label="Instances"><input className="field__control" type="number" min="1" step="1" value={instances} disabled={native} onChange={(event) => change('instances', Number(event.target.value))} />{fieldError('Instances') && <span className="field-error">{fieldError('Instances')}</span>}</Field>
      <Field label="Storage per instance (GiB)"><input className="field__control" type="number" min={cluster.spec.storage} step="1" value={storage} disabled={native} onChange={(event) => change('storage', Number(event.target.value))} />{fieldError('Storage') && <span className="field-error">{fieldError('Storage')}</span>}</Field>
      <Field label="Compute profile" hint="Keeping the current profile preserves its resolved resource snapshot."><select className="field__control" value={computeProfileId} onChange={(event) => change('computeProfileId', event.target.value)}>
        <option value="">Keep current · {cluster.placement.compute.displayName}</option>{compute.resource?.map((profile) => <option key={profile.id} value={profile.id}>{profile.displayName}</option>)}</select>{fieldError('ComputeProfileId') && <span className="field-error">{fieldError('ComputeProfileId')}</span>}</Field>
    </div></fieldset>
    {compute.error && <Notice tone="error">{compute.error}<Button onClick={() => void compute.refresh()}>Retry compute profiles</Button></Notice>}
    {validation && <Notice tone="error">{validation}</Notice>}{!conflict && mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    <div className="row-actions"><Button variant="primary" type="submit" disabled={mutation.busy || !dirty || conflict || awaitingRefresh}>{mutation.busy ? 'Saving…' : 'Save changes'}</Button><Button disabled={mutation.busy || awaitingRefresh} onClick={() => setDiscarding(true)}>Discard changes</Button></div>
    </form>
    {reapplying && <ConfirmationDialog title="Reapply your changes?" confirmLabel="Reapply changes" onCancel={() => setReapplying(false)} onConfirm={() => {
      setConfiguration(reapplyPostgresChanges(configuration, cluster)); mutation.clearError(); setValidation(''); setReapplying(false)
    }}>Only your changed fields ({Object.keys(changes).filter((field) => field !== 'expectedRevision').join(', ')}) will be applied to current revision {cluster.revision}. Unchanged fields keep the current server values. Review the result, then save.</ConfirmationDialog>}
    {discarding && <ConfirmationDialog title="Discard database changes?" confirmLabel="Discard changes" onCancel={() => setDiscarding(false)} onConfirm={() => {
      setConfiguration(postgresConfiguration(cluster)); mutation.clearError(); setValidation(''); setDiscarding(false)
    }}>The current server configuration will replace your unsaved draft.</ConfirmationDialog>}
  </section>
}
