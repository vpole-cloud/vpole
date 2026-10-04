import { ResourceHealth } from '../../ui/ResourceHealth'
import { ResourceOperations } from '../../ui/ResourceOperations'
import { useEffect, useState } from 'react'
import { deleteRedisService, fetchAvailableComputeProfiles, fetchAvailableDeploymentTargets, fetchAvailableStorageProfiles, fetchIsolationScopes, fetchRedisService, fetchResourceGroup, retryResourceDeployment, updateRedisService, validationMessage } from '../../api'
import { useResource } from '../../hooks/useResource'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import type { RedisService } from '../../types'
import { Button, ConfirmationDialog, EmptyState, Field, Notice, StatusBadge } from '../../ui/components'
import { PlacementSummary } from '../../ui/PlacementSummary'
import { ResourceEndpoints } from '../../ui/ResourceEndpoints'
import { Tabs, TabPanel } from '../../ui/Tabs'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { parseRedisTags, redisType, type RedisSection } from './workspaceState'

export function RedisResourceView({ id, section, active, version, onSection, actions }: { id: string; section: RedisSection; active: boolean; version: number; onSection: (section: RedisSection) => void; actions: WorkspaceActions<RedisSection> }) {
  const { api, tenant, navigation } = useAppServices()
  const scopeKey = `${api.baseUrl}:${api.tenantId}:${api.token}:${id}`
  const details = useResource(scopeKey, (signal) => fetchRedisService(api.baseUrl, id, api.token, api.tenantId, signal), version)
  const redis = details.resource
  const placement = redis?.placement
  const group = useResource(`${scopeKey}:${redis?.resourceGroupId}`, (signal) => redis ? fetchResourceGroup(api.baseUrl, redis.resourceGroupId, api.token, api.tenantId, signal) : Promise.resolve(null))
  const scopes = useResource(`${scopeKey}:${redis?.resourceGroupId}:scopes`, (signal) => redis ? fetchIsolationScopes(api.baseUrl, redis.resourceGroupId, api.token, api.tenantId, signal) : Promise.resolve([]))
  const targets = useResource(`${scopeKey}:${placement?.isolationScopeId}`, (signal) => placement ? fetchAvailableDeploymentTargets(api.baseUrl, placement.isolationScopeId ?? undefined, api.token, api.tenantId, signal, false, redisType) : Promise.resolve([]))
  const storage = useResource(`${scopeKey}:${placement?.deploymentTargetId}:storage`, (signal) => placement ? fetchAvailableStorageProfiles(api.baseUrl, redisType, placement.deploymentTargetId, api.token, api.tenantId, signal) : Promise.resolve([]))
  const mutation = useScopedMutation(scopeKey)
  const [deleting, setDeleting] = useState(false)
  const [visited, setVisited] = useState(section === 'configuration')
  const [dirty, setDirty] = useState(false)
  if (section === 'configuration' && !visited) setVisited(true)
  const onLoaded = actions.loaded
  const onDirty = actions.dirty
  useEffect(() => { if (redis) onLoaded(redis.id, redis.name) }, [redis, onLoaded])
  useEffect(() => { onDirty(id, dirty); return () => onDirty(id, false) }, [id, dirty, onDirty])
  const refresh = details.refresh
  const pending = redis && (redis.lifecycle === 'Deleting' || placement && ['WaitingForRuntimeCell', 'Pending', 'Reconciling', 'Deleting', 'Failed'].includes(placement.status))
  useEffect(() => { if (!active || !pending) return; const timer = window.setInterval(() => void refresh(), 2500); return () => window.clearInterval(timer) }, [active, pending, refresh])
  if (!redis) return <EmptyState title={details.status === 'error' ? 'Unable to open this Redis service.' : 'Loading Redis service…'} error={details.status === 'error'}>{details.error && <p>{details.error}</p>}<Button onClick={() => void details.refresh()}>Retry Redis service</Button></EmptyState>
  const canWrite = tenant.canManage && redis.lifecycle === 'Active'
  const panelId = `redis-${id}`
  return <div><header className="ui-page-header"><div><p className="ui-page-eyebrow">Redis · {group.resource?.name ?? 'Resource group'}</p><h1>{redis.name}</h1><p>Persistent single-instance Redis service</p></div><div className="row-actions"><Button disabled={details.status === 'loading'} onClick={() => void details.refresh()}>Refresh Redis</Button>{canWrite && <Button variant="danger" onClick={() => setDeleting(true)}>Delete Redis</Button>}</div></header>
    {details.error && <Notice tone="error">{details.error} Last fetched metadata is shown.</Notice>}
    {redis.lifecycle === 'Deleting' && <Notice>This service is being deleted. Wait for cleanup to complete.</Notice>}
    <Tabs id={panelId} label={`${redis.name} sections`} variant="section" activeId={section} onSelect={(value) => onSection(value as RedisSection)} tabs={[{ id: 'overview', label: 'Overview' }, { id: 'operations', label: 'Operations' }, { id: 'configuration', label: 'Configuration' }]} />
    <TabPanel id={panelId} tabId="overview" activeId={section}><section className="panel"><div className="panel__header"><h2 className="panel__title">Redis overview</h2><StatusBadge>{redis.lifecycle}</StatusBadge></div><dl className="web-service-facts">
      <div><dt>Resource group</dt><dd><a href={navigation.resourceHref({ resourceTypeId: 'pole/resource-group', resourceId: redis.resourceGroupId })}>{group.resource?.name ?? redis.resourceGroupId}</a></dd></div>
      <div><dt>Isolation scope</dt><dd>{scopes.resource?.find((item) => item.id === placement?.isolationScopeId)?.name ?? placement?.isolationScopeId ?? 'Unavailable'}</dd></div>
      <div><dt>Storage</dt><dd>{redis.spec.storage.sizeGb} GiB</dd></div><div><dt>Storage profile</dt><dd>{storage.resource?.find((item) => item.id === redis.spec.storage.storageProfileId)?.displayName ?? redis.spec.storage.storageProfileId}</dd></div>
      <div><dt>Tags</dt><dd>{redis.tags.join(', ') || 'No tags'}</dd></div><div><dt>Revision</dt><dd>{redis.revision}</dd></div></dl><p className="field__hint">Application network access and workload configuration are managed separately.</p></section>
      {placement ? <PlacementSummary placement={placement} targetName={targets.resource?.find((item) => item.id === placement.deploymentTargetId)?.displayName} /> : <Notice>Deployment metadata is unavailable. No readiness result is known.</Notice>}
      <ResourceHealth placements={placement ? [placement] : []} active={active && section === 'overview'} refreshVersion={version} targetNames={Object.fromEntries((targets.resource ?? []).map((target) => [target.id, target.displayName]))} />
      {active && section === 'overview' && <ResourceEndpoints resourceType={redisType} resourceId={id} />}
      {tenant.canManage && placement?.status === 'Failed' && <Button disabled={mutation.busy} onClick={() => {
        void mutation.run((signal) => retryResourceDeployment(api.baseUrl, placement.deploymentId, placement.deploymentRevision ?? 1, placement.desiredOperation ?? "Apply", api.token, api.tenantId, signal), async () => { await details.refresh(); actions.changed() }).catch(() => {})
      }}>Retry deployment</Button>}
    </TabPanel>
    <TabPanel id={panelId} tabId="operations" activeId={section}>{active && section === "operations" && <ResourceOperations filter={{ resourceType: redisType, resourceId: id }} onRetry={(operation, signal) => retryResourceDeployment(api.baseUrl, operation.deployment.id, operation.deployment.placement.deploymentRevision!, operation.deployment.placement.desiredOperation!, api.token, api.tenantId, signal)} />}</TabPanel>
    <TabPanel id={panelId} tabId="configuration" activeId={section}>{visited && (canWrite && placement ? <RedisConfiguration redis={redis} onDirtyChange={setDirty} onSaved={async () => { await details.refresh(); actions.changed() }} /> : <EmptyState title="Redis configuration is read-only." />)}</TabPanel>
    {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    {deleting && canWrite && <ConfirmationDialog title={`Delete ${redis.name}?`} confirmLabel="Delete Redis" busy={mutation.busy} onCancel={() => setDeleting(false)} onConfirm={() => {
      void mutation.run((signal) => deleteRedisService(api.baseUrl, id, api.token, api.tenantId, signal), async () => { setDeleting(false); await details.refresh(); actions.changed() }).catch(() => {})
    }}>This removes the Redis workload and its storage. Applications using it will lose access.</ConfirmationDialog>}
  </div>
}

function RedisConfiguration({ redis, onDirtyChange, onSaved }: { redis: RedisService; onDirtyChange: (dirty: boolean) => void; onSaved: () => Promise<void> }) {
  const { api, notifications } = useAppServices()
  const scopeKey = `${api.baseUrl}:${api.tenantId}:${api.token}:${redis.id}`
  const targetId = redis.placement!.deploymentTargetId
  const compute = useResource(`${scopeKey}:${targetId}`, (signal) => fetchAvailableComputeProfiles(api.baseUrl, targetId, api.token, api.tenantId, signal, redisType))
  const [tags, setTags] = useState(redis.tags.join(', '))
  const [computeId, setComputeId] = useState('')
  const snapshot = JSON.stringify([tags, computeId])
  const [saved, setSaved] = useState(snapshot)
  const [revision, setRevision] = useState(redis.revision)
  const dirty = snapshot !== saved
  if (!dirty && revision !== redis.revision) {
    setRevision(redis.revision); setTags(redis.tags.join(', ')); setComputeId(''); setSaved(JSON.stringify([redis.tags.join(', '), '']))
  }
  useUnsavedChanges(dirty, redis.name)
  useEffect(() => { onDirtyChange(dirty); return () => onDirtyChange(false) }, [onDirtyChange, dirty])
  const mutation = useScopedMutation(scopeKey)
  const [discarding, setDiscarding] = useState(false)
  const [validation, setValidation] = useState('')
  const error = (field: string) => validationMessage(mutation.error, field)
  return <section className="panel"><h2 className="panel__title">Redis configuration</h2><p className="field__hint">Name, group, target and storage are fixed after creation. This native StatefulSet does not support storage resizing. Compute and tags can be updated.</p>
    <form className="database-form" onSubmit={(event) => {
      event.preventDefault(); if (mutation.busy) return
      if (computeId && (compute.status !== 'success' || !compute.resource?.some((item) => item.id === computeId))) { setValidation('Choose an available compute profile.'); return }
      setValidation('')
      void mutation.run((signal) => updateRedisService(api.baseUrl, redis.id, { spec: redis.spec, tags: parseRedisTags(tags), computeProfileId: computeId || undefined }, api.token, api.tenantId, signal), async () => {
        setSaved(snapshot); await onSaved(); notifications.publish(`Configuration accepted for ${redis.name}.`)
      }).catch(() => {})
    }}><fieldset disabled={mutation.busy}><div className="form-grid">
      <Field label="Storage (GiB)"><input className="field__control" type="number" readOnly disabled value={redis.spec.storage.sizeGb} />{error('Spec.Storage.SizeGb') && <span className="field-error">{error('Spec.Storage.SizeGb')}</span>}</Field>
      <Field label="Compute profile" hint="Keeping the current profile preserves its resolved snapshot."><select className="field__control" value={computeId} onChange={(event) => setComputeId(event.target.value)}><option value="">Keep current · {redis.placement!.compute.displayName}</option>{compute.resource?.map((item) => <option key={item.id} value={item.id}>{item.displayName}</option>)}</select>{error('ComputeProfileId') && <span className="field-error">{error('ComputeProfileId')}</span>}</Field>
      <Field label="Tags" hint="Separate tags with commas."><input className="field__control" value={tags} onChange={(event) => setTags(event.target.value)} /></Field>
    </div></fieldset>
    {compute.error && <Notice tone="error">{compute.error}<Button onClick={() => void compute.refresh()}>Retry compute profiles</Button></Notice>}
    {validation && <Notice tone="error">{validation}</Notice>}{mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    <div className="row-actions"><Button type="submit" variant="primary" disabled={mutation.busy || !dirty}>{mutation.busy ? 'Saving…' : 'Save changes'}</Button><Button disabled={mutation.busy} onClick={() => setDiscarding(true)}>Discard changes</Button></div></form>
    {discarding && <ConfirmationDialog title="Discard Redis changes?" confirmLabel="Discard changes" onCancel={() => setDiscarding(false)} onConfirm={() => {
      setTags(redis.tags.join(', ')); setComputeId(''); setSaved(JSON.stringify([redis.tags.join(', '), ''])); mutation.clearError(); setDiscarding(false)
    }}>The current server configuration will replace your unsaved draft.</ConfirmationDialog>}
  </section>
}
