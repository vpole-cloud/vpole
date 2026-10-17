import { useEffect, useState } from 'react'
import { createRedisService, validationMessage } from '../../api'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { useResourceChoices } from '../../data/useResourceChoices'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { Button, Field, Notice } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { parseRedisTags, redisType, type RedisSection } from './workspaceState'

export function NewRedisView({
  actions,
  active,
}: {
  actions: WorkspaceActions<RedisSection>
  active: boolean
}) {
  const { api } = useAppServices()
  const origin = useProviderInvalidation(api)
  const scopeKey = `${origin.identity}:create-redis`
  const [name, setName] = useState('')
  const [tags, setTags] = useState('')
  const [groupId, setGroupId] = useState('')
  const [scopeId, setScopeId] = useState('')
  const [targetId, setTargetId] = useState('')
  const [computeId, setComputeId] = useState('')
  const [storageId, setStorageId] = useState('')
  const {
    groups,
    scopes,
    targets,
    storage,
    compute,
    group,
    isolation,
    target,
    computeProfile,
    storageProfile,
    profiles,
    allowed,
  } = useResourceChoices(
    api,
    redisType,
    {
      groupId,
      isolationScopeId: scopeId,
      deploymentTargetId: targetId,
      computeProfileId: computeId,
      storageProfileId: storageId,
    },
    active,
  )
  const [size, setSize] = useState(1)
  const [submitted, setSubmitted] = useState(false)
  const [validation, setValidation] = useState('')
  const mutation = useScopedMutation(scopeKey)
  const dirty =
    !submitted &&
    Boolean(name || tags || groupId || scopeId || targetId || computeId || storageId || size !== 1)
  const onDirty = actions.dirty
  useUnsavedChanges(dirty, 'New Redis service')
  useEffect(() => {
    onDirty('create', dirty)
    return () => onDirty('create', false)
  }, [dirty, onDirty])
  const resetTarget = () => {
    setTargetId('')
    setComputeId('')
    setStorageId('')
  }
  const error = (field: string) => validationMessage(mutation.error, field)
  return (
    <section className="panel">
      <header className="ui-page-header">
        <div>
          <p className="ui-page-eyebrow">Redis</p>
          <h1>Create Redis service</h1>
          <p>One persistent Redis instance on one deployment target.</p>
        </div>
      </header>
      <form
        className="database-form"
        onSubmit={(event) => {
          event.preventDefault()
          if (mutation.busy) return
          if (
            !allowed ||
            !/^[a-z0-9-]{1,30}$/.test(name.trim()) ||
            !Number.isInteger(size) ||
            size < 1 ||
            size > 1000
          ) {
            setValidation(
              'Use a name of up to 30 lowercase letters, digits or hyphens and 1–1000 GiB of storage. Select available placement and profiles.',
            )
            return
          }
          setValidation('')
          void mutation
            .run(
              async (signal) => {
                const created = await createRedisService(
                  api.baseUrl,
                  {
                    name: name.trim(),
                    resourceGroupId: group,
                    tags: parseRedisTags(tags),
                    spec: { storage: { sizeGb: size, storageProfileId: storageProfile } },
                    computeProfileId: computeProfile,
                    placement: { isolationScopeId: isolation, deploymentTargetId: target },
                  },
                  origin.credential(),
                  api.tenantId,
                  signal,
                )
                await origin.invalidate([['redis'], ['resource-groups'], ['overview'], ['search']])
                return created
              },
              (created) => {
                if (!origin.isCurrent()) return
                setSubmitted(true)
                actions.created(created.redisId, name.trim())
              },
            )
            .catch(() => {})
        }}
      >
        <fieldset disabled={mutation.busy}>
          <div className="form-grid">
            <Field label="Name" hint="Up to 30 lowercase letters, digits or hyphens.">
              <input
                className="field__control"
                required
                maxLength={30}
                pattern="[a-z0-9-]+"
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
              {error('Name') && <span className="field-error">{error('Name')}</span>}
            </Field>
            <Field label="Resource group">
              <select
                className="field__control"
                required
                value={group}
                onChange={(event) => {
                  setGroupId(event.target.value)
                  setScopeId('')
                  resetTarget()
                }}
              >
                {!groups.items.length && <option value="">No available groups</option>}
                {groups.items.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.name}
                  </option>
                ))}
              </select>
              {error('ResourceGroupId') && (
                <span className="field-error">{error('ResourceGroupId')}</span>
              )}
            </Field>
            <Field label="Isolation scope">
              <select
                className="field__control"
                value={isolation}
                onChange={(event) => {
                  setScopeId(event.target.value)
                  resetTarget()
                }}
              >
                {!scopes.items.length && <option value="">No available scopes</option>}
                {scopes.items
                  .filter((item) => item.lifecycle === 'active')
                  .map((item) => (
                    <option key={item.id} value={item.id}>
                      {item.name}
                      {item.isDefault ? ' (default)' : ''}
                    </option>
                  ))}
              </select>
              {error('Placement.IsolationScopeId') && (
                <span className="field-error">{error('Placement.IsolationScopeId')}</span>
              )}
            </Field>
            <Field label="Deployment target">
              <select
                className="field__control"
                required
                value={target}
                onChange={(event) => {
                  setTargetId(event.target.value)
                  setComputeId('')
                  setStorageId('')
                }}
              >
                <option value="">Select a target</option>
                {targets.resource?.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.displayName}
                  </option>
                ))}
              </select>
              {error('Placement.DeploymentTargetId') && (
                <span className="field-error">{error('Placement.DeploymentTargetId')}</span>
              )}
            </Field>
            <Field label="Storage (GiB)">
              <input
                className="field__control"
                type="number"
                required
                min="1"
                max="1000"
                step="1"
                value={size}
                onChange={(event) => setSize(Number(event.target.value))}
              />
              {error('Spec.Storage.SizeGb') && (
                <span className="field-error">{error('Spec.Storage.SizeGb')}</span>
              )}
            </Field>
            <Field label="Storage profile">
              <select
                className="field__control"
                required
                value={storageProfile}
                onChange={(event) => setStorageId(event.target.value)}
              >
                <option value="">Select a profile</option>
                {profiles.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.displayName}
                    {item.isDefault ? ' (default)' : ''}
                  </option>
                ))}
              </select>
              {error('Spec.Storage.StorageProfileId') && (
                <span className="field-error">{error('Spec.Storage.StorageProfileId')}</span>
              )}
            </Field>
            <Field label="Compute profile">
              <select
                className="field__control"
                required
                value={computeProfile}
                onChange={(event) => setComputeId(event.target.value)}
              >
                <option value="">Select a profile</option>
                {compute.resource?.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.displayName}
                    {item.isDefault ? ' (default)' : ''}
                  </option>
                ))}
              </select>
              {error('ComputeProfileId') && (
                <span className="field-error">{error('ComputeProfileId')}</span>
              )}
            </Field>
            <Field label="Tags" hint="Separate tags with commas.">
              <input
                className="field__control"
                value={tags}
                onChange={(event) => setTags(event.target.value)}
              />
            </Field>
          </div>
        </fieldset>
        {[groups.error, scopes.error, targets.error, storage.error, compute.error]
          .filter(Boolean)
          .map((error, index) => (
            <Notice key={index} tone="error">
              {error}
            </Notice>
          ))}
        {!allowed && (
          <Notice>
            Select an available group, isolation scope, deployment target, storage profile and
            compute profile to continue. If choices are missing, ask a platform administrator to
            prepare them.
          </Notice>
        )}
        {validation && <Notice tone="error">{validation}</Notice>}
        {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
        <div className="row-actions">
          <Button type="submit" variant="primary" disabled={mutation.busy || !allowed}>
            {mutation.busy ? 'Creating…' : 'Create Redis'}
          </Button>
          <Button
            onClick={() => {
              void groups.refresh()
              scopes.retry()
              void targets.refresh()
              void storage.refresh()
              void compute.refresh()
            }}
          >
            Refresh choices
          </Button>
        </div>
      </form>
    </section>
  )
}
