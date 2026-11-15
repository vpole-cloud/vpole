import { useEffect, useState } from 'react'
import {
  createSeaweedFS,
  getSeaweedFSCapacity,
  fetchPostgresManagedDatabaseCandidates,
  validationMessage,
} from '../../api'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useResourceChoices } from '../../data/useResourceChoices'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { Button, Field, Notice } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { seaweedFSType, type SeaweedFSMode } from './types'
import type { SeaweedFSSection } from './SeaweedFSView'

export function NewSeaweedFSView({
  actions,
  active,
}: {
  actions: WorkspaceActions<SeaweedFSSection>
  active: boolean
}) {
  const { api } = useAppServices()
  const origin = useProviderInvalidation(api)
  const scopeKey = `${origin.identity}:create-seaweedfs`
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
    seaweedFSType,
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
  const [mode, setMode] = useState<SeaweedFSMode>('SingleNode')
  const [metadataChoice, setMetadataChoice] = useState('dedicated')
  const [postgresId, setPostgresId] = useState('')
  const usesExistingPostgres = mode === 'Replicated' && metadataChoice === 'existing'
  const candidates = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['postgres', 'metadata-candidates', target],
    enabled: active && usesExistingPostgres && Boolean(api.tenantId && target),
    load: (signal) =>
      fetchPostgresManagedDatabaseCandidates(
        api.baseUrl,
        target,
        origin.credential(),
        api.tenantId,
        signal,
      ),
  })
  const candidateSelected =
    !usesExistingPostgres ||
    (candidates.isCurrent &&
      candidates.resource?.some(
        (item) => item.id === postgresId && item.deploymentTargetId === target,
      ))
  const capacity = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: ['seaweedfs', 'capacity', mode, size, usesExistingPostgres],
    active,
    enabled: Boolean(api.tenantId) && Number.isInteger(size) && size > 0 && size <= 16384,
    load: (signal) =>
      getSeaweedFSCapacity(
        { ...api, token: origin.credential() },
        mode,
        size,
        usesExistingPostgres,
        signal,
      ),
  })
  const [submitted, setSubmitted] = useState(false)
  const [validation, setValidation] = useState('')
  const mutation = useScopedMutation(scopeKey)
  const dirty =
    !submitted &&
    Boolean(
      name ||
      tags ||
      groupId ||
      scopeId ||
      targetId ||
      computeId ||
      storageId ||
      size !== 1 ||
      mode !== 'SingleNode',
    )
  const onDirty = actions.dirty
  useUnsavedChanges(dirty, 'New SeaweedFS instance')
  useEffect(() => {
    onDirty('create', dirty)
    return () => onDirty('create', false)
  }, [dirty, onDirty])
  const resetTarget = () => {
    setPostgresId('')
    setTargetId('')
    setComputeId('')
    setStorageId('')
  }
  const error = (field: string) => validationMessage(mutation.error, field)
  return (
    <section className="panel">
      <header className="ui-page-header">
        <div>
          <p className="ui-page-eyebrow">SeaweedFS</p>
          <h1>Create SeaweedFS instance</h1>
        </div>
      </header>
      <form
        className="database-form"
        onSubmit={(event) => {
          event.preventDefault()
          if (mutation.busy) return
          if (
            !capacity.isCurrent ||
            !allowed ||
            !candidateSelected ||
            !/^[a-z0-9-]{1,30}$/.test(name.trim()) ||
            !Number.isInteger(size) ||
            size < 1 ||
            size > 16384
          ) {
            setValidation(
              'Use a name of up to 30 lowercase letters, digits or hyphens and 1–16384 GiB of storage. Select available placement and profiles.',
            )
            return
          }
          setValidation('')
          void mutation
            .run(
              async (signal) => {
                const created = await createSeaweedFS(
                  { ...api, token: origin.credential() },
                  {
                    name: name.trim(),
                    resourceGroupId: group,
                    tags: [
                      ...new Set(
                        tags
                          .split(',')
                          .map((tag) => tag.trim())
                          .filter(Boolean),
                      ),
                    ],
                    spec: {
                      mode,
                      ...(usesExistingPostgres ? { existingPostgresId: postgresId } : {}),
                      storage: { sizeGb: size, storageProfileId: storageProfile },
                    },
                    computeProfileId: computeProfile,
                    placement: { isolationScopeId: isolation, deploymentTargetId: target },
                  },
                  signal,
                )
                return created
              },
              (created) => {
                if (!origin.isCurrent()) return
                setSubmitted(true)
                actions.created(created.seaweedFSId, name.trim())
                void origin.invalidate([
                  ['seaweedfs'],
                  ['resource-groups'],
                  ['overview'],
                  ['search'],
                ])
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
                  setPostgresId('')
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
            <Field label="Deployment mode">
              <select
                className="field__control"
                value={mode}
                onChange={(event) => {
                  setPostgresId('')
                  setMetadataChoice('dedicated')
                  setMode(event.target.value === 'Replicated' ? 'Replicated' : 'SingleNode')
                }}
              >
                <option value="SingleNode">Single node</option>
                <option value="Replicated">Replicated on three nodes</option>
              </select>
            </Field>
            {mode === 'Replicated' && (
              <Field label="Filer metadata storage">
                <select
                  className="field__control"
                  value={metadataChoice}
                  onChange={(event) => {
                    setMetadataChoice(event.target.value)
                    setPostgresId('')
                  }}
                >
                  <option value="dedicated">Create a dedicated CNPG cluster</option>
                  <option value="existing">Use an existing PostgreSQL cluster</option>
                </select>
              </Field>
            )}
            {usesExistingPostgres && (
              <Field
                label="PostgreSQL cluster"
                hint="Pole creates a separate database and role on a synchronously replicated cluster in this target."
              >
                <select
                  className="field__control"
                  required
                  value={postgresId}
                  onChange={(event) => setPostgresId(event.target.value)}
                >
                  <option value="">Select a cluster</option>
                  {candidates.resource?.map((item) => (
                    <option key={item.id} value={item.id}>
                      {item.name}
                    </option>
                  ))}
                </select>
                {candidates.isCurrent && candidates.resource?.length === 0 && (
                  <span>No eligible PostgreSQL clusters on this target.</span>
                )}
                {candidates.error && (
                  <span className="field-error">Could not load PostgreSQL clusters.</span>
                )}
                {error('Spec.ExistingPostgresId') && (
                  <span className="field-error">{error('Spec.ExistingPostgresId')}</span>
                )}
              </Field>
            )}
            <Field label="Data disk per volume server (GiB)">
              <input
                className="field__control"
                type="number"
                required
                min="1"
                max="16384"
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
        {capacity.isCurrent && capacity.resource && (
          <p>
            {capacity.resource.storageGb} GiB of storage and {capacity.resource.workloadInstances}{' '}
            workload pods requested.
          </p>
        )}
        {capacity.error && <Notice tone="error">Unable to calculate required capacity.</Notice>}
        {validation && <Notice tone="error">{validation}</Notice>}
        {mutation.error instanceof Error && <Notice tone="error">{mutation.error.message}</Notice>}
        <div className="row-actions">
          <Button
            type="submit"
            variant="primary"
            disabled={mutation.busy || !allowed || !capacity.isCurrent || !candidateSelected}
          >
            {mutation.busy ? 'Creating…' : 'Create SeaweedFS'}
          </Button>
          <Button
            onClick={() => {
              void groups.refresh()
              scopes.retry()
              void targets.refresh()
              void storage.refresh()
              void compute.refresh()
              if (usesExistingPostgres) void candidates.refresh()
            }}
          >
            Refresh choices
          </Button>
        </div>
      </form>
    </section>
  )
}
