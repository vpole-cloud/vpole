import { useEffect, useState } from 'react'
import { createSecret, fetchIsolationScopes } from '../../api'
import { fetchResourceGroups } from '../../api'
import { useSecretQuery, useSecretScope } from './queries'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { Button, Field, Notice } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { SecretValues } from './SecretValues'
import { useClearOnLeave, useWriteOnlyValues, validateValues } from './useWriteOnlyValues'
import type { SecretSection } from './workspaceState'

export function NewSecretView({ active, actions }: { active: boolean; actions: WorkspaceActions<SecretSection> }) {
  return active ? <SecretCreateForm actions={actions} /> : null
}

function SecretCreateForm({ actions }: { actions: WorkspaceActions<SecretSection> }) {
  const { api } = useAppServices()
  const origin = useSecretScope()
  const groupsQuery = useSecretQuery(['resource-groups', 'inventory', 1, 6, ''], (token, signal) => fetchResourceGroups(api.baseUrl, token, api.tenantId, signal))
  const groups = { ...groupsQuery, items: groupsQuery.resource?.items ?? [] }
  const [name, setName] = useState(''); const [purpose, setPurpose] = useState(''); const [groupId, setGroupId] = useState(''); const [scopeId, setScopeId] = useState('')
  const group = groupId || groups.items[0]?.id || ''
  const scopeQuery = useSecretQuery(['resource-groups', 'scopes', group], (token, signal) => fetchIsolationScopes(api.baseUrl, group, token, api.tenantId, signal), 0, Boolean(group))
  const scopes = { ...scopeQuery, items: scopeQuery.resource ?? [] }
  const isolation = scopeId || scopes.items.find((item) => item.isDefault)?.id || ''
  const values = useWriteOnlyValues()
  const mutation = useScopedMutation(origin.writeIdentity)
  useClearOnLeave(values.clear, mutation.cancel)
  const [validation, setValidation] = useState(''); const [submitted, setSubmitted] = useState(false)
  const dirty = !submitted && Boolean(name || purpose || values.rows.some((row) => row.key || row.value))
  const onDirty = actions.dirty
  useUnsavedChanges(dirty, 'New Secret')
  useEffect(() => { onDirty('create', dirty); return () => onDirty('create', false) }, [onDirty, dirty])
  const allowed = groups.status === 'success' && scopes.status === 'success' && groups.items.some((item) => item.id === group) && scopes.items.some((item) => item.id === isolation && item.lifecycle === 'active')
  return <section className="panel"><header className="ui-page-header"><div><p className="ui-page-eyebrow">Secrets</p><h1>Create Secret</h1><p>Store encrypted values in one resource group and isolation scope.</p></div></header>
    <form className="database-form" onSubmit={(event) => {
      event.preventDefault(); if (mutation.busy) return
      const error = validateValues(values.rows); if (!allowed || !name.trim() || error) { setValidation(error || 'Choose an available group and scope and enter a name.'); return }
      setValidation('')
      void mutation.run((signal) => createSecret(api.baseUrl, { name: name.trim(), purpose: purpose.trim(), resourceGroupId: group, isolationScopeId: isolation,
        values: Object.fromEntries(values.rows.map((row) => [row.key, row.value])) }, origin.credential(), api.tenantId, signal), async (created) => {
        values.clear(); setSubmitted(true);
        await origin.session.client.invalidateQueries({ queryKey: origin.key(['secrets']), refetchType: 'active' })
        if (!origin.isCurrent()) return
        actions.created(created.id, created.name)
      }).catch(() => {})
    }}><fieldset disabled={mutation.busy}><div className="form-grid">
      <Field label="Name"><input className="field__control" required maxLength={200} value={name} onChange={(event) => setName(event.target.value)} /></Field>
      <Field label="Purpose"><input className="field__control" maxLength={500} value={purpose} onChange={(event) => setPurpose(event.target.value)} /></Field>
      <Field label="Resource group"><select className="field__control" required value={group} onChange={(event) => { setGroupId(event.target.value); setScopeId('') }}><option value="">Select a group</option>{groups.items.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></Field>
      <Field label="Isolation scope"><select className="field__control" required value={isolation} onChange={(event) => setScopeId(event.target.value)}><option value="">Select a scope</option>{scopes.items.filter((item) => item.lifecycle === 'active').map((item) => <option key={item.id} value={item.id}>{item.name}{item.isDefault ? ' (default)' : ''}</option>)}</select></Field>
    </div><SecretValues rows={values.rows} onChange={values.setRows} cleared={values.cleared} /></fieldset>
      {[groups.error, scopes.error, validation, mutation.error instanceof Error ? mutation.error.message : ''].filter(Boolean).map((error, index) => <Notice key={index} tone="error">{error}</Notice>)}
      <div className="row-actions"><Button type="submit" variant="primary" disabled={mutation.busy || !allowed}>{mutation.busy ? 'Creating…' : 'Create Secret'}</Button><Button onClick={() => { void groups.refresh(); void scopes.refresh() }}>Refresh choices</Button></div>
    </form></section>
}
