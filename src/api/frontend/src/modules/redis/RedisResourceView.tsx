import { eligibilityKeys } from '../../data/queryKeys'
import { ResourceHealth } from '../../ui/ResourceHealth'
import { ResourceOperations } from '../../ui/ResourceOperations'
import { useEffect, useState } from 'react'
import { deleteRedisService, fetchAvailableComputeProfiles, fetchAvailableDeploymentTargets, fetchAvailableStorageProfiles, fetchIsolationScopes, fetchRedisService, fetchResourceGroup, retryResourceDeployment, updateRedisService, validationMessage } from '../../api'
import { useScopedQuery } from '../../data/useScopedQuery'
import { groupInterests, resourceInterests } from '../../realtime/resourceInterests'
import { useVisibleRefresh } from '../../hooks/useVisibleRefresh'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
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
import { acceptedRedisChanges, isRedisDirty, reapplyRedisChanges, redisConfiguration } from './configurationModel'

export function RedisResourceView({ id, section, active, onSection, actions }: { id: string; section: RedisSection; active: boolean; onSection: (section: RedisSection) => void; actions: WorkspaceActions<RedisSection> }) {
  const { api, tenant, navigation } = useAppServices()
  const origin = useProviderInvalidation(api)
  const scopeKey = `${origin.identity}:${id}:${tenant.canManage}`
  const scope = [api.baseUrl, api.tenantId]
  const realtime = resourceInterests('redis', api.tenantId, id)
  const details = useScopedQuery<RedisService>({ scope, key: ['redis', 'detail', id], active, realtime, resourceGroupId: (value) => value.resourceGroupId,
    load: (signal) => fetchRedisService(api.baseUrl, id, origin.credential(), api.tenantId, signal) })
  const redis = details.resource
  const placement = redis?.placement
  const interest = { active, realtime: groupInterests(api.tenantId, redis?.resourceGroupId), resourceGroupId: redis?.resourceGroupId }
  const group = useScopedQuery({ scope, key: ['resource-groups', 'detail', redis?.resourceGroupId], enabled: Boolean(redis), ...interest, load: (signal) => fetchResourceGroup(api.baseUrl, redis!.resourceGroupId, origin.credential(), api.tenantId, signal) })
  const scopes = useScopedQuery({ scope, key: eligibilityKeys.scopes(redis?.resourceGroupId), enabled: Boolean(redis), ...interest, load: (signal) => fetchIsolationScopes(api.baseUrl, redis!.resourceGroupId, origin.credential(), api.tenantId, signal) })
  const targets = useScopedQuery({ scope, key: eligibilityKeys.targets(redisType, placement?.isolationScopeId, false), enabled: Boolean(placement), ...interest, realtime: [...interest.realtime, { topic: 'deployment-target-eligibility', tenantId: api.tenantId }], load: (signal) => fetchAvailableDeploymentTargets(api.baseUrl, placement!.isolationScopeId ?? undefined, origin.credential(), api.tenantId, signal, false, redisType) })
  const storage = useScopedQuery({ scope, key: eligibilityKeys.storage(redisType, placement?.deploymentTargetId), enabled: Boolean(placement), ...interest, realtime: [...interest.realtime, { topic: 'storage-eligibility', tenantId: api.tenantId }], load: (signal) => fetchAvailableStorageProfiles(api.baseUrl, redisType, placement!.deploymentTargetId, origin.credential(), api.tenantId, signal) })
  const mutation = useScopedMutation(scopeKey)
  const [deleting, setDeleting] = useState(false)
  const [visited, setVisited] = useState(section === 'configuration')
  const [dirty, setDirty] = useState(false)
  if (section === 'configuration' && !visited) setVisited(true)
  const onLoaded = actions.loaded
  const onDirty = actions.dirty
  useEffect(() => { if (redis) onLoaded(redis.id, redis.name) }, [redis, onLoaded])
  useEffect(() => { onDirty(id, dirty); return () => onDirty(id, false) }, [id, dirty, onDirty])
  const pending = Boolean(redis && (redis.lifecycle === 'Deleting' || placement && ['WaitingForRuntimeCell', 'Pending', 'Reconciling', 'Deleting', 'Failed'].includes(placement.status)))
  useVisibleRefresh(active && pending, details.refresh, 2500, realtime)
  const afterWrite = async () => {
    await Promise.all([details.refreshAfterWrite(), origin.invalidate([['redis', 'inventory'], ['resource-groups'], ['overview'], ['search'], ['operations'], ['health'], ['endpoints']])])

  }
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
      <ResourceHealth resourceType="pole/redis" placements={placement ? [placement] : []} active={active && section === 'overview'} targetNames={Object.fromEntries((targets.resource ?? []).map((target) => [target.id, target.displayName]))} />
      {active && section === 'overview' && <ResourceEndpoints resourceType={redisType} resourceId={id} />}
      {tenant.canManage && placement?.status === 'Failed' && <Button disabled={mutation.busy} onClick={() => {
        void mutation.run((signal) => retryResourceDeployment(api.baseUrl, placement.deploymentId, placement.deploymentRevision ?? 1, placement.desiredOperation ?? "Apply", origin.credential(), api.tenantId, signal), afterWrite).catch(() => {})
      }}>Retry deployment</Button>}
    </TabPanel>
    <TabPanel id={panelId} tabId="operations" activeId={section}>{active && section === "operations" && <ResourceOperations filter={{ resourceType: redisType, resourceId: id }} onAccepted={afterWrite} onRetry={(operation, signal) => retryResourceDeployment(api.baseUrl, operation.deployment.id, operation.deployment.placement.deploymentRevision!, operation.deployment.placement.desiredOperation!, origin.credential(), api.tenantId, signal)} />}</TabPanel>
    <TabPanel id={panelId} tabId="configuration" activeId={section}>{visited && (canWrite && placement ? <RedisConfiguration redis={redis} active={active && section === 'configuration'} onDirtyChange={setDirty} onSaved={afterWrite} /> : <EmptyState title="Redis configuration is read-only." />)}</TabPanel>
    {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    {deleting && canWrite && <ConfirmationDialog title={`Delete ${redis.name}?`} confirmLabel="Delete Redis" busy={mutation.busy} onCancel={() => setDeleting(false)} onConfirm={() => {
      void mutation.run((signal) => deleteRedisService(api.baseUrl, id, origin.credential(), api.tenantId, signal), async () => { setDeleting(false); await afterWrite() }).catch(() => {})
    }}>This removes the Redis workload and its storage. Applications using it will lose access.</ConfirmationDialog>}
  </div>
}

function RedisConfiguration({ redis, active, onDirtyChange, onSaved }: { redis: RedisService; active: boolean; onDirtyChange: (dirty: boolean) => void; onSaved: () => Promise<void> }) {
  const { api, notifications } = useAppServices()
  const origin = useProviderInvalidation(api)
  const scopeKey = `${origin.identity}:${redis.id}`
  const targetId = redis.placement!.deploymentTargetId
  const compute = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: eligibilityKeys.compute(redisType, targetId), active,
    realtime: [...groupInterests(api.tenantId, redis.resourceGroupId), { topic: 'compute-eligibility', tenantId: api.tenantId }], resourceGroupId: redis.resourceGroupId,
    load: (signal) => fetchAvailableComputeProfiles(api.baseUrl, targetId, origin.credential(), api.tenantId, signal, redisType) })
  const [configuration, setConfiguration] = useState(() => redisConfiguration(redis))
  const { tags, computeProfileId: computeId } = configuration.draft
  const dirty = isRedisDirty(configuration)
  if (!dirty && configuration.baseline.revision !== redis.revision) setConfiguration(redisConfiguration(redis))
  const conflict = dirty && configuration.baseline.revision !== redis.revision
  const awaitingRefresh = configuration.accepted && configuration.baseline.revision === redis.revision
  const setTags = (tags: string) => setConfiguration((current) => ({ ...current, draft: { ...current.draft, tags } }))
  const setComputeId = (computeProfileId: string) => setConfiguration((current) => ({ ...current, draft: { ...current.draft, computeProfileId } }))
  useUnsavedChanges(dirty, redis.name)
  useEffect(() => { onDirtyChange(dirty); return () => onDirtyChange(false) }, [onDirtyChange, dirty])
  const mutation = useScopedMutation(scopeKey)
  const [discarding, setDiscarding] = useState(false)
  const [reapplying, setReapplying] = useState(false)
  const [validation, setValidation] = useState('')
  const error = (field: string) => validationMessage(mutation.error, field)
  return <section className="panel"><h2 className="panel__title">Redis configuration</h2><p className="field__hint">Name, group, target and storage are fixed after creation. This native StatefulSet does not support storage resizing. Compute and tags can be updated.</p>
    {conflict && <Notice tone="error">The Redis configuration changed since revision {configuration.baseline.revision}. Your draft is preserved.
      <p>Server tags: {redis.tags.join(', ') || 'No tags'} · Draft tags: {tags || 'No tags'}. Current compute: {redis.placement!.compute.displayName}.</p>
      <Button onClick={() => setReapplying(true)}>Review and reapply draft</Button></Notice>}
    {awaitingRefresh && <Notice>Configuration was accepted. Refresh Redis to load its new revision.</Notice>}
    <form className="database-form" onSubmit={(event) => {
      event.preventDefault(); if (mutation.busy || !dirty || conflict || awaitingRefresh) return
      if (computeId && (compute.status !== 'success' || !compute.resource?.some((item) => item.id === computeId))) { setValidation('Choose an available compute profile.'); return }
      setValidation('')
      const submitted = configuration
      void mutation.run((signal) => updateRedisService(api.baseUrl, redis.id, { spec: redis.spec, tags: parseRedisTags(tags), computeProfileId: computeId || undefined }, origin.credential(), api.tenantId, signal), async () => {
        setConfiguration(acceptedRedisChanges(submitted)); await onSaved(); if (!origin.isCurrent()) return; notifications.publish(`Configuration accepted for ${redis.name}.`)
      }).catch(() => {})
    }}><fieldset disabled={mutation.busy || awaitingRefresh}><div className="form-grid">
      <Field label="Storage (GiB)"><input className="field__control" type="number" readOnly disabled value={redis.spec.storage.sizeGb} />{error('Spec.Storage.SizeGb') && <span className="field-error">{error('Spec.Storage.SizeGb')}</span>}</Field>
      <Field label="Compute profile" hint="Keeping the current profile preserves its resolved snapshot."><select className="field__control" value={computeId} onChange={(event) => setComputeId(event.target.value)}><option value="">Keep current · {redis.placement!.compute.displayName}</option>{compute.resource?.map((item) => <option key={item.id} value={item.id}>{item.displayName}</option>)}</select>{error('ComputeProfileId') && <span className="field-error">{error('ComputeProfileId')}</span>}</Field>
      <Field label="Tags" hint="Separate tags with commas."><input className="field__control" value={tags} onChange={(event) => setTags(event.target.value)} /></Field>
    </div></fieldset>
    {compute.error && <Notice tone="error">{compute.error}<Button onClick={() => void compute.refresh()}>Retry compute profiles</Button></Notice>}
    {validation && <Notice tone="error">{validation}</Notice>}{mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    <div className="row-actions"><Button type="submit" variant="primary" disabled={mutation.busy || !dirty || conflict || awaitingRefresh}>{mutation.busy ? 'Saving…' : 'Save changes'}</Button><Button disabled={mutation.busy || awaitingRefresh} onClick={() => setDiscarding(true)}>Discard changes</Button></div></form>
    {reapplying && <ConfirmationDialog title="Reapply your Redis changes?" confirmLabel="Reapply changes" onCancel={() => setReapplying(false)} onConfirm={() => {
      setConfiguration(reapplyRedisChanges(configuration, redis)); mutation.clearError(); setValidation(''); setReapplying(false)
    }}>Your changed tags and compute choice will be kept. Unchanged tags use the current server values. Review the result, then save.</ConfirmationDialog>}
    {discarding && <ConfirmationDialog title="Discard Redis changes?" confirmLabel="Discard changes" onCancel={() => setDiscarding(false)} onConfirm={() => {
      setConfiguration(redisConfiguration(redis)); mutation.clearError(); setValidation(''); setDiscarding(false)
    }}>The current server configuration will replace your unsaved draft.</ConfirmationDialog>}
  </section>
}
