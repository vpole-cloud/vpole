import { useCallback, useEffect, useState } from 'react'
import { createRedisService, fetchAvailableComputeProfiles, fetchAvailableDeploymentTargets, fetchAvailableStorageProfiles, fetchIsolationScopes, validationMessage } from '../../api'
import { useResource } from '../../hooks/useResource'
import { useResourceGroupsManager } from '../../hooks/useResourceGroupsManager'
import { useIsolationScopes } from '../../hooks/useIsolationScopes'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { Button, Field, Notice } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { parseRedisTags, redisType, type RedisSection } from './workspaceState'

export function NewRedisView({ actions }: { actions: WorkspaceActions<RedisSection>; active: boolean }) {
  const { api } = useAppServices()
  const scopeKey = `${api.baseUrl}:${api.tenantId}:${api.token}`
  const groups = useResourceGroupsManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: true })
  const [name, setName] = useState('')
  const [tags, setTags] = useState('')
  const [groupId, setGroupId] = useState('')
  const group = groupId || groups.items[0]?.id || ''
  const [scopeId, setScopeId] = useState('')
  const loadScopes = useCallback((id: string, signal?: AbortSignal) => fetchIsolationScopes(api.baseUrl, id, api.token, api.tenantId, signal), [api.baseUrl, api.token, api.tenantId])
  const scopes = useIsolationScopes(group, loadScopes)
  const isolation = scopeId || scopes.items.find((scope) => scope.isDefault)?.id || ''
  const targets = useResource(`${scopeKey}:${group}:${isolation}`, (signal) => fetchAvailableDeploymentTargets(api.baseUrl, isolation || undefined, api.token, api.tenantId, signal, false, redisType))
  const [targetId, setTargetId] = useState('')
  const target = targetId || (targets.resource?.length === 1 ? targets.resource[0].id : '')
  const storage = useResource(`${scopeKey}:${target}:storage`, (signal) => target ? fetchAvailableStorageProfiles(api.baseUrl, redisType, target, api.token, api.tenantId, signal) : Promise.resolve([]))
  const compute = useResource(`${scopeKey}:${target}:compute`, (signal) => target ? fetchAvailableComputeProfiles(api.baseUrl, target, api.token, api.tenantId, signal, redisType) : Promise.resolve([]))
  const [computeId, setComputeId] = useState('')
  const computeProfile = computeId || compute.resource?.find((profile) => profile.isDefault)?.id || (compute.resource?.length === 1 ? compute.resource[0].id : '')
  const [storageId, setStorageId] = useState('')
  const profiles = storage.resource?.filter((profile) => !profile.targetMappings || profile.targetMappings.some((mapping) => mapping.deploymentTargetId === target)) ?? []
  const storageProfile = storageId || profiles.find((profile) => profile.isDefault)?.id || (profiles.length === 1 ? profiles[0].id : '')
  const [size, setSize] = useState(1)
  const [submitted, setSubmitted] = useState(false)
  const [validation, setValidation] = useState('')
  const mutation = useScopedMutation(scopeKey)
  const dirty = !submitted && Boolean(name || tags || groupId || scopeId || targetId || computeId || storageId || size !== 1)
  const onDirty = actions.dirty
  useUnsavedChanges(dirty, 'New Redis service')
  useEffect(() => { onDirty('create', dirty); return () => onDirty('create', false) }, [dirty, onDirty])
  const resetTarget = () => { setTargetId(''); setComputeId(''); setStorageId('') }
  const allowed = groups.status === 'success' && scopes.status === 'success' && targets.status === 'success' && storage.status === 'success' && compute.status === 'success' &&
    groups.items.some((item) => item.id === group) && scopes.items.some((item) => item.id === isolation && item.lifecycle === 'active') && targets.resource?.some((item) => item.id === target) &&
    compute.resource?.some((item) => item.id === computeProfile) && profiles.some((item) => item.id === storageProfile)
  const error = (field: string) => validationMessage(mutation.error, field)
  return <section className="panel"><header className="ui-page-header"><div><p className="ui-page-eyebrow">Redis</p><h1>Create Redis service</h1><p>One persistent Redis instance on one deployment target.</p></div></header>
    <form className="database-form" onSubmit={(event) => {
      event.preventDefault(); if (mutation.busy) return
      if (!allowed || !/^[a-z0-9-]{1,30}$/.test(name.trim()) || !Number.isInteger(size) || size < 1 || size > 1000) { setValidation('Use a name of up to 30 lowercase letters, digits or hyphens and 1–1000 GiB of storage. Select available placement and profiles.'); return }
      setValidation('')
      void mutation.run((signal) => createRedisService(api.baseUrl, { name: name.trim(), resourceGroupId: group, tags: parseRedisTags(tags),
        spec: { storage: { sizeGb: size, storageProfileId: storageProfile } }, computeProfileId: computeProfile,
        placement: { isolationScopeId: isolation, deploymentTargetId: target } }, api.token, api.tenantId, signal), (created) => {
        setSubmitted(true); actions.created(created.redisId, name.trim())
      }).catch(() => {})
    }}><fieldset disabled={mutation.busy}><div className="form-grid">
      <Field label="Name" hint="Up to 30 lowercase letters, digits or hyphens."><input className="field__control" required maxLength={30} pattern="[a-z0-9-]+" value={name} onChange={(event) => setName(event.target.value)} />{error('Name') && <span className="field-error">{error('Name')}</span>}</Field>
      <Field label="Resource group"><select className="field__control" required value={group} onChange={(event) => { setGroupId(event.target.value); setScopeId(''); resetTarget() }}>
        {!groups.items.length && <option value="">No available groups</option>}{groups.items.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select>{error('ResourceGroupId') && <span className="field-error">{error('ResourceGroupId')}</span>}</Field>
      <Field label="Isolation scope"><select className="field__control" value={isolation} onChange={(event) => { setScopeId(event.target.value); resetTarget() }}>
        {!scopes.items.length && <option value="">No available scopes</option>}{scopes.items.filter((item) => item.lifecycle === 'active').map((item) => <option key={item.id} value={item.id}>{item.name}{item.isDefault ? ' (default)' : ''}</option>)}</select>{error('Placement.IsolationScopeId') && <span className="field-error">{error('Placement.IsolationScopeId')}</span>}</Field>
      <Field label="Deployment target"><select className="field__control" required value={target} onChange={(event) => { setTargetId(event.target.value); setComputeId(''); setStorageId('') }}>
        <option value="">Select a target</option>{targets.resource?.map((item) => <option key={item.id} value={item.id}>{item.displayName}</option>)}</select>{error('Placement.DeploymentTargetId') && <span className="field-error">{error('Placement.DeploymentTargetId')}</span>}</Field>
      <Field label="Storage (GiB)"><input className="field__control" type="number" required min="1" max="1000" step="1" value={size} onChange={(event) => setSize(Number(event.target.value))} />{error('Spec.Storage.SizeGb') && <span className="field-error">{error('Spec.Storage.SizeGb')}</span>}</Field>
      <Field label="Storage profile"><select className="field__control" required value={storageProfile} onChange={(event) => setStorageId(event.target.value)}><option value="">Select a profile</option>{profiles.map((item) => <option key={item.id} value={item.id}>{item.displayName}{item.isDefault ? ' (default)' : ''}</option>)}</select>{error('Spec.Storage.StorageProfileId') && <span className="field-error">{error('Spec.Storage.StorageProfileId')}</span>}</Field>
      <Field label="Compute profile"><select className="field__control" required value={computeProfile} onChange={(event) => setComputeId(event.target.value)}><option value="">Select a profile</option>{compute.resource?.map((item) => <option key={item.id} value={item.id}>{item.displayName}{item.isDefault ? ' (default)' : ''}</option>)}</select>{error('ComputeProfileId') && <span className="field-error">{error('ComputeProfileId')}</span>}</Field>
      <Field label="Tags" hint="Separate tags with commas."><input className="field__control" value={tags} onChange={(event) => setTags(event.target.value)} /></Field>
    </div></fieldset>
    {[groups.error, scopes.error, targets.error, storage.error, compute.error].filter(Boolean).map((error, index) => <Notice key={index} tone="error">{error}</Notice>)}
    {!allowed && <Notice>Available groups, scopes, targets and profiles are required. Ask a platform administrator to prepare any missing prerequisites.</Notice>}
    {validation && <Notice tone="error">{validation}</Notice>}{mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
    <div className="row-actions"><Button type="submit" variant="primary" disabled={mutation.busy || !allowed}>{mutation.busy ? 'Creating…' : 'Create Redis'}</Button>
      <Button onClick={() => { void groups.refresh(); scopes.retry(); void targets.refresh(); void storage.refresh(); void compute.refresh() }}>Refresh choices</Button></div>
    </form></section>
}
