import { useEffect, useState } from 'react'
import { createPostgresCluster, fetchPostgresCapabilities, validationMessage } from '../../api'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { useResourceChoices } from '../../data/useResourceChoices'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { Button, Field, Notice } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import type { PostgresSection } from './workspaceState'

const resourceType = 'pole/postgresql'

export function NewPostgresView({ actions, active }: { actions: WorkspaceActions<PostgresSection>; active: boolean }) {
  const { api } = useAppServices()
  const origin = useProviderInvalidation(api)
  const scopeKey = `${origin.identity}:create-postgres`
  const capabilities = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: ['postgres', 'capabilities'], load: (signal) => fetchPostgresCapabilities(api.baseUrl, origin.credential(), api.tenantId, signal) })
  const [name, setName] = useState('')
  const [groupId, setGroupId] = useState('')
  const [isolationScopeId, setIsolationScopeId] = useState('')
  const [targetId, setTargetId] = useState('')
  const [computeId, setComputeId] = useState('')
  const [storageId, setStorageId] = useState('')
  const { groups, scopes, targets, storage, compute, group, isolation, target, computeProfile, storageProfile, profiles: storageProfiles, allowed: choicesReady } = useResourceChoices(api, resourceType,
    { groupId, isolationScopeId, deploymentTargetId: targetId, computeProfileId: computeId, storageProfileId: storageId }, active)
  const [version, setVersion] = useState<number | null>(null)
  const selectedVersion = version ?? capabilities.resource?.to ?? 0
  const [instances, setInstances] = useState(1)
  const [size, setSize] = useState(1)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [validation, setValidation] = useState('')
  const [submitted, setSubmitted] = useState(false)
  const mutation = useScopedMutation(scopeKey)
  const onDirty = actions.dirty
  const dirty = !submitted && Boolean(name || username || password || groupId || isolationScopeId || targetId || computeId || storageId || version || instances !== 1 || size !== 1)
  useUnsavedChanges(dirty, 'New PostgreSQL database')
  useEffect(() => { onDirty('create', dirty); return () => onDirty('create', false) }, [onDirty, dirty])
  // Sensitive inputs are never retained in an inactive tab.
  useEffect(() => { if (!active) queueMicrotask(() => { setPassword(''); setUsername('') }) }, [active])
  const fieldError = (field: string) => validationMessage(mutation.error, field)
  const allowed = capabilities.status === 'success' && capabilities.isCurrent && choicesReady
  const versions = capabilities.resource ? Array.from({ length: Math.max(0, capabilities.resource.to - capabilities.resource.from + 1) }, (_, index) => capabilities.resource!.from + index) : []
  const native = capabilities.resource?.defaultDeploymentMode === 'StatefulSet'
  return <section className="panel"><header className="ui-page-header"><div><p className="ui-page-eyebrow">PostgreSQL</p><h1>Create PostgreSQL database</h1>
    <p>{capabilities.status !== 'success' ? 'Loading provisioning options…' : native ? 'Native StatefulSet · one instance. Password rotation is unavailable for an initialized native database.' : 'CloudNativePG manages replication and password rotation. The target needs a configured PostgreSQL operator.'}</p></div></header>
    <form className="database-form" onSubmit={(event) => {
      event.preventDefault(); if (mutation.busy) return
      if (!allowed || !name.trim() || !username.trim() || !password || !Number.isInteger(size) || size < 1 || !Number.isInteger(instances) || instances < 1) { setValidation('Complete required fields and select available placement and profiles.'); return }
      setValidation('')
      void mutation.run(async (signal) => { const created = await createPostgresCluster(api.baseUrl, { name: name.trim(), groupId: group, version: selectedVersion, instances: native ? 1 : instances,
        storage: size, storageProfileId: storageProfile, computeProfileId: computeProfile, username: username.trim(), password,
        placement: { isolationScopeId: isolation, deploymentTargetId: target } }, origin.credential(), api.tenantId, signal); await origin.invalidate([['postgres'], ['resource-groups'], ['overview'], ['search']]); return created }, (cluster) => {
        if (!origin.isCurrent()) return
        setPassword(''); setUsername(''); setSubmitted(true); actions.created(cluster.id, cluster.name)
      }).catch(() => {})
    }}>
      <fieldset disabled={mutation.busy}><div className="form-grid">
        <Field label="Name"><input className="field__control" value={name} onChange={(event) => setName(event.target.value)} required aria-invalid={Boolean(fieldError('Name'))} />{fieldError('Name') && <span className="field-error">{fieldError('Name')}</span>}</Field>
        <Field label="Resource group"><select className="field__control" value={group} onChange={(event) => { setGroupId(event.target.value); setIsolationScopeId(''); setTargetId(''); setComputeId(''); setStorageId('') }} required>
          {!groups.items.length && <option value="">No available groups</option>}{groups.items.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}</select>{fieldError('GroupId') && <span className="field-error">{fieldError('GroupId')}</span>}</Field>
        <Field label="Isolation scope" hint="Resources in the same scope share a runtime boundary."><select className="field__control" value={isolation} onChange={(event) => { setIsolationScopeId(event.target.value); setTargetId(''); setComputeId(''); setStorageId('') }}>
          {!scopes.items.length && <option value="">No available scopes</option>}{scopes.items.filter((scope) => scope.lifecycle === 'active').map((scope) => <option key={scope.id} value={scope.id}>{scope.name}{scope.isDefault ? ' (default)' : ''}</option>)}</select>{fieldError('Placement.IsolationScopeId') && <span className="field-error">{fieldError('Placement.IsolationScopeId')}</span>}</Field>
        <Field label="Deployment target"><select className="field__control" value={target} onChange={(event) => { setTargetId(event.target.value); setComputeId(''); setStorageId('') }} required>
          <option value="">Select a target</option>{targets.resource?.map((target) => <option key={target.id} value={target.id}>{target.displayName}</option>)}</select>{fieldError('Placement.DeploymentTargetId') && <span className="field-error">{fieldError('Placement.DeploymentTargetId')}</span>}</Field>
        <Field label="Version"><select className="field__control" value={selectedVersion} onChange={(event) => setVersion(Number(event.target.value))}>{versions.map((version) => <option key={version} value={version}>PostgreSQL {version}</option>)}</select>{fieldError('Version') && <span className="field-error">{fieldError('Version')}</span>}</Field>
        <Field label="Instances"><input className="field__control" type="number" min="1" step="1" value={native ? 1 : instances} disabled={native} onChange={(event) => setInstances(Number(event.target.value))} />{fieldError('Instances') && <span className="field-error">{fieldError('Instances')}</span>}</Field>
        <Field label="Storage per instance (GiB)"><input className="field__control" type="number" min="1" step="1" value={size} onChange={(event) => setSize(Number(event.target.value))} />{fieldError('Storage') && <span className="field-error">{fieldError('Storage')}</span>}</Field>
        <Field label="Storage profile"><select className="field__control" value={storageProfile} onChange={(event) => setStorageId(event.target.value)} required><option value="">Select a profile</option>{storageProfiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.displayName}{profile.isDefault ? ' (default)' : ''}</option>)}</select>{fieldError('StorageProfileId') && <span className="field-error">{fieldError('StorageProfileId')}</span>}</Field>
        <Field label="Compute profile"><select className="field__control" value={computeProfile} onChange={(event) => setComputeId(event.target.value)} required><option value="">Select a profile</option>{compute.resource?.map((profile) => <option key={profile.id} value={profile.id}>{profile.displayName}{profile.isDefault ? ' (default)' : ''}</option>)}</select>{fieldError('ComputeProfileId') && <span className="field-error">{fieldError('ComputeProfileId')}</span>}</Field>
      </div></fieldset>
      <fieldset disabled={mutation.busy}><legend>Initial credentials</legend><p className="field__hint">Values remain only in this active form. Closing or leaving this tab clears them.</p>
        <div className="form-grid"><Field label="Database username"><input className="field__control" autoComplete="off" value={username} onChange={(event) => setUsername(event.target.value)} required />{fieldError('Username') && <span className="field-error">{fieldError('Username')}</span>}</Field>
          <Field label="Database password"><input className="field__control" type="password" autoComplete="new-password" value={password} onChange={(event) => setPassword(event.target.value)} required />{fieldError('Password') && <span className="field-error">{fieldError('Password')}</span>}</Field></div>
      </fieldset>
      {[groups.error, capabilities.error, scopes.error, targets.error, storage.error, compute.error].filter(Boolean).map((error, index) => <Notice key={index} tone="error">{error}</Notice>)}
      {!allowed && <Notice>Select an available group, isolation scope, deployment target, storage profile and compute profile to continue. If choices are missing, ask a platform administrator to prepare them.</Notice>}
      {validation && <Notice tone="error">{validation}</Notice>}{mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
      <div className="row-actions"><Button variant="primary" type="submit" disabled={mutation.busy || !allowed}>{mutation.busy ? 'Creating…' : 'Create PostgreSQL'}</Button>
        <Button onClick={() => { void groups.refresh(); void capabilities.refresh(); void storage.refresh(); scopes.retry(); void targets.refresh(); void compute.refresh() }}>Refresh choices</Button></div>
    </form>
  </section>
}
