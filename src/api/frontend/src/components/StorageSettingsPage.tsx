import { useMemo, useState, type FormEvent } from 'react'
import type {
  KubernetesPersistentVolume,
  KubernetesPersistentVolumeClaim,
  KubernetesStorageClass,
  LoadStatus,
  PoleResourceType,
  StorageMediaType,
  StoragePerformanceTier,
  StorageProfile,
  StorageProfileInput,
  StorageProfileTenantAccessMode,
  PlatformDeploymentTarget,
} from '../types'
import { ConfirmationDialog, Field } from '../ui/components'
import { useUnsavedChanges } from '../shell/useUnsavedChanges'

interface StorageSettingsPageProps {
  profiles: StorageProfile[]
  targets: PlatformDeploymentTarget[]
  deploymentTargetId: string
  onSelectTarget: (id: string) => void
  storageClasses: KubernetesStorageClass[]
  persistentVolumes: KubernetesPersistentVolume[]
  persistentVolumeClaims: KubernetesPersistentVolumeClaim[]
  status: LoadStatus
  error?: string
  submitStatus: LoadStatus
  submitError?: string
  onRefresh: () => Promise<void>
  onCreate: (input: StorageProfileInput) => Promise<void>
  onUpdate: (id: string, input: StorageProfileInput) => Promise<void>
  canManage?: boolean
}

const mediaTypes: StorageMediaType[] = ['Unknown', 'HDD', 'SSD', 'NVMe', 'Network']
const performanceTiers: StoragePerformanceTier[] = ['Standard', 'Bulk', 'Fast', 'Ultra']
const resourceTypes: PoleResourceType[] = ['pole/redis', 'pole/postgresql']

const parseList = (value: string) =>
  value
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)

const toggleValue = <T extends string>(current: T[], value: T) =>
  current.includes(value)
    ? current.filter((item) => item !== value)
    : [...current, value]

const ownerLabel = (
  owner: KubernetesPersistentVolume['owner'] | KubernetesPersistentVolumeClaim['owner'],
) => {
  const parts = [
    owner.resourceType,
    owner.resourceName,
    owner.resourceId,
    owner.resourceGroupId,
    owner.tenantId,
  ].filter(Boolean)

  return parts.length > 0 ? parts.join(' / ') : 'Unmanaged'
}

const profileResourceLabel = (profile: StorageProfile) =>
  profile.allowedResourceTypes.length > 0
    ? profile.allowedResourceTypes.join(', ')
    : 'No resource types'

const profileTenantLabel = (profile: StorageProfile) =>
  profile.tenantAccess.mode === 'AllTenants'
    ? 'All tenants'
    : profile.tenantAccess.tenantIds.join(', ') || 'No tenants'

export function StorageSettingsPage({
  profiles,
  targets,
  deploymentTargetId,
  onSelectTarget,
  storageClasses,
  persistentVolumes,
  persistentVolumeClaims,
  status,
  error,
  submitStatus,
  submitError,
  onRefresh,
  onCreate,
  onUpdate,
  canManage = false,
}: StorageSettingsPageProps) {
  const [query, setQuery] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [formGeneration, setFormGeneration] = useState(0)
  const [pending, setPending] = useState<{ run: () => void } | null>(null)
  useUnsavedChanges(dirty, 'Storage profile')
  const change = (run: () => void) => {
    const transition = () => { setDirty(false); setFormGeneration((current) => current + 1); run() }
    if (dirty) setPending({ run: transition })
    else transition()
  }
  const classLabel = (profile: StorageProfile) => profile.targetMappings.map((mapping) =>
    `${targets.find((target) => target.id === mapping.deploymentTargetId)?.displayName ?? 'Unavailable target'}: ${mapping.storageClassName}`).join(', ')

  const filteredProfiles = profiles.filter((profile) =>
    [
      profile.displayName,
      profile.description,
      classLabel(profile),
      profile.allowedResourceTypes.join(' '),
      profileTenantLabel(profile),
    ]
      .join(' ')
      .toLowerCase()
      .includes(query.trim().toLowerCase()),
  )

  const selectedProfile =
    filteredProfiles.find((profile) => profile.id === selectedId) ??
    profiles.find((profile) => profile.id === selectedId) ??
    null

  return (
    <div className="resource-groups-page">
      <section className="hero-panel">
        <div className="hero-panel__main">
          <p className="hero-panel__eyebrow">Storage</p>
          <h1 className="hero-panel__title">Volume profiles</h1>
          <p className="hero-panel__description">
            Discover cluster storage classes, publish tenant-safe volume
            profiles, and keep resource storage selections tied to managed
            policy.
          </p>
        </div>

        <aside className="hero-panel__utility">
          <div className="session-card">
            <div>
              <p className="session-card__label">Platform tools</p>
              <p className="session-card__value">Storage inventory</p>
            </div>
            <div className="session-card__actions">
              <button className="button" disabled={!canManage || submitStatus === 'loading'} onClick={() => change(() => { void onRefresh() })} type="button">
                Refresh
              </button>
              <button
                className="button"
                disabled={!canManage || submitStatus === 'loading'}
                onClick={() => change(() => setSelectedId(null))}
                type="button"
              >
                New profile
              </button>
            </div>
            <p className="session-card__note">
              Profiles marked as default are the first choice resource forms can
              preselect for new volumes.
            </p>
          </div>
        </aside>
      </section>

      {canManage && <Field label="Deployment target" hint="Inventory and the edited storage class belong to this target. Other target mappings are preserved.">
        <select className="field__control" value={deploymentTargetId} disabled={submitStatus === 'loading'}
          onChange={(event) => { const id = event.target.value; change(() => onSelectTarget(id)) }}>
          {targets.length === 0 && <option value="">No deployment targets</option>}
          {targets.map((target) => <option key={target.id} value={target.id}>{target.displayName}{target.enabled ? '' : ' · Disabled'}</option>)}
        </select>
      </Field>}

      {!canManage && (
        <section className="panel">
          <div className="empty-state empty-state--error">
            Platform administrator access is required for storage settings.
          </div>
        </section>
      )}

      <div className="storage-summary-grid">
        <section className="summary-panel">
          <p className="summary-panel__label">Profiles</p>
          <p className="summary-panel__value">{profiles.length}</p>
          <p className="summary-panel__helper">
            {profiles.filter((profile) => profile.isEnabled).length} enabled
          </p>
        </section>
        <section className="summary-panel">
          <p className="summary-panel__label">Storage classes</p>
          <p className="summary-panel__value">{storageClasses.length}</p>
          <p className="summary-panel__helper">
            {storageClasses.find((item) => item.isDefault)?.name ?? 'No default'}
          </p>
        </section>
        <section className="summary-panel">
          <p className="summary-panel__label">Persistent volumes</p>
          <p className="summary-panel__value">{persistentVolumes.length}</p>
          <p className="summary-panel__helper">
            {persistentVolumes.filter((item) => item.owner.resourceId).length} managed
          </p>
        </section>
        <section className="summary-panel">
          <p className="summary-panel__label">Volume claims</p>
          <p className="summary-panel__value">{persistentVolumeClaims.length}</p>
          <p className="summary-panel__helper">
            {persistentVolumeClaims.filter((item) => item.owner.resourceId).length} managed
          </p>
        </section>
      </div>

      <div className="resource-groups-layout resource-groups-layout--wide-form">
        <section className="panel resource-groups-list-panel">
          <div className="panel__header">
            <div>
              <p className="panel__eyebrow">Policy</p>
              <h2 className="panel__title">Storage profiles</h2>
            </div>
            <span className="panel__meta">{filteredProfiles.length} visible</span>
          </div>

          <label className="field">
            <span className="field__label">Search</span>
            <input
              className="field__control"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Find by name, class, tenant, or resource type"
            />
          </label>

          {status === 'loading' && (
            <div className="empty-state">Loading storage settings...</div>
          )}

          {status === 'error' && (
            <div className="empty-state empty-state--error">
              {error || 'Unable to load storage settings.'}
            </div>
          )}

          {status === 'success' && filteredProfiles.length === 0 && (
            <div className="empty-state">
              {profiles.length === 0
                ? 'No storage profiles exist yet.'
                : 'No storage profiles match this search.'}
            </div>
          )}

          {status === 'success' && filteredProfiles.length > 0 && (
            <div className="list">
              {filteredProfiles.map((profile) => (
                <article
                  className={`list-row list-row--interactive${selectedId === profile.id ? ' is-selected' : ''}`}
                  key={profile.id}
                >
                  <button
                    className="list-row__button"
                    disabled={submitStatus === 'loading'} onClick={() => change(() => setSelectedId(profile.id))}
                    type="button"
                  >
                    <div className="list-row__primary">
                      <h3>{profile.displayName}</h3>
                      <p>{profile.description || 'No description provided yet.'}</p>
                    </div>
                    <div className="list-row__secondary">
                      <span
                        className={`status-pill${profile.isDefault ? ' status-pill--accent' : ''}`}
                      >
                        {profile.isDefault ? 'Default' : profile.isEnabled ? 'Enabled' : 'Disabled'}
                      </span>
                      <span className="list-row__meta">
                        {classLabel(profile)} · {profileResourceLabel(profile)}
                      </span>
                    </div>
                  </button>

                  <div className="row-actions">
                    <button className="button" disabled={!canManage || submitStatus === 'loading'} onClick={() => change(() => setSelectedId(profile.id))} type="button">
                      Edit
                    </button>
                  </div>
                </article>
              ))}
            </div>
          )}
        </section>

        {canManage && deploymentTargetId && status === 'success' ? (
          <StorageProfileForm
            key={`${selectedProfile?.id ?? 'create'}:${selectedProfile?.revision ?? 0}:${deploymentTargetId}:${formGeneration}`}
            deploymentTargetId={deploymentTargetId}
            onDirtyChange={setDirty}
            mode={selectedProfile ? 'edit' : 'create'}
            initialValue={selectedProfile}
            storageClasses={storageClasses}
            submitStatus={submitStatus}
            submitError={submitError}
            onSubmit={async (input) => {
              if (selectedProfile) {
                await onUpdate(selectedProfile.id, input)
                setDirty(false)
                return
              }

              await onCreate(input)
              setDirty(false)
            }}
            onCancelEdit={selectedProfile ? () => change(() => setSelectedId(null)) : undefined}
          />
        ) : (
          <section className="panel">
            <div className="empty-state">{canManage ? 'Load a deployment target to edit profiles.' : 'Storage profile editing requires platform administrator access.'}</div>
          </section>
        )}
      </div>

      {pending && <ConfirmationDialog title="Discard storage changes?" confirmLabel="Discard changes"
        onCancel={() => setPending(null)} onConfirm={() => { setDirty(false); pending.run(); setPending(null) }}>
        The unsaved storage profile will be discarded.
      </ConfirmationDialog>}
      <div className="storage-inventory-grid">
        <section className="panel">
          <div className="panel__header">
            <div>
              <p className="panel__eyebrow">Kubernetes</p>
              <h2 className="panel__title">Storage classes</h2>
            </div>
            <span className="panel__meta">{storageClasses.length} total</span>
          </div>
          <div className="storage-table">
            {storageClasses.map((item) => (
              <div className="storage-table__row" key={item.name}>
                <div>
                  <strong>{item.name}</strong>
                  <span>{item.provisioner}</span>
                </div>
                <span>{item.isDefault ? 'Default' : item.volumeBindingMode ?? 'Class'}</span>
                <span>{item.allowVolumeExpansion ? 'Expandable' : item.reclaimPolicy ?? 'Policy unset'}</span>
              </div>
            ))}
            {status === 'success' && storageClasses.length === 0 && (
              <div className="empty-state">No storage classes discovered.</div>
            )}
          </div>
        </section>

        <section className="panel">
          <div className="panel__header">
            <div>
              <p className="panel__eyebrow">Kubernetes</p>
              <h2 className="panel__title">Volume claims</h2>
            </div>
            <span className="panel__meta">{persistentVolumeClaims.length} total</span>
          </div>
          <div className="storage-table">
            {persistentVolumeClaims.slice(0, 8).map((item) => (
              <div className="storage-table__row" key={`${item.namespace}/${item.name}`}>
                <div>
                  <strong>{item.name}</strong>
                  <span>{item.namespace}</span>
                </div>
                <span>{item.storageClassName ?? 'No class'}</span>
                <span>{item.requestedCapacity ?? item.capacity ?? item.status ?? 'No capacity'}</span>
                <span>{ownerLabel(item.owner)}</span>
              </div>
            ))}
            {status === 'success' && persistentVolumeClaims.length === 0 && (
              <div className="empty-state">No volume claims discovered.</div>
            )}
          </div>
        </section>
      </div>
    </div>
  )
}

interface StorageProfileFormProps {
  deploymentTargetId: string
  onDirtyChange: (dirty: boolean) => void
  mode: 'create' | 'edit'
  initialValue?: StorageProfile | null
  storageClasses: KubernetesStorageClass[]
  submitStatus: LoadStatus
  submitError?: string
  onSubmit: (input: StorageProfileInput) => Promise<void>
  onCancelEdit?: () => void
}

function StorageProfileForm({
  deploymentTargetId,
  onDirtyChange,
  mode,
  initialValue,
  storageClasses,
  submitStatus,
  submitError,
  onSubmit,
  onCancelEdit,
}: StorageProfileFormProps) {
  const defaultStorageClassName = useMemo(
    () => storageClasses.find((item) => item.isDefault)?.name ?? storageClasses[0]?.name ?? '',
    [storageClasses],
  )
  const [displayName, setDisplayName] = useState(initialValue?.displayName ?? '')
  const [description, setDescription] = useState(initialValue?.description ?? '')
  const [mediaType, setMediaType] = useState<StorageMediaType>(
    initialValue?.mediaType ?? 'SSD',
  )
  const [performanceTier, setPerformanceTier] = useState<StoragePerformanceTier>(
    initialValue?.performanceTier ?? 'Standard',
  )
  const [isEnabled, setIsEnabled] = useState(initialValue?.isEnabled ?? true)
  const [isDefault, setIsDefault] = useState(initialValue?.isDefault ?? false)
  const initialClass = initialValue?.targetMappings.find((mapping) => mapping.deploymentTargetId === deploymentTargetId)?.storageClassName
  const [storageClassName, setStorageClassName] = useState(initialClass ?? defaultStorageClassName)
  const [allowedResourceTypes, setAllowedResourceTypes] = useState<PoleResourceType[]>(
    initialValue?.allowedResourceTypes ?? ['pole/redis', 'pole/postgresql'],
  )
  const [tenantAccessMode, setTenantAccessMode] =
    useState<StorageProfileTenantAccessMode>(
      initialValue?.tenantAccess.mode ?? 'AllTenants',
    )
  const [tenantIdsText, setTenantIdsText] = useState(
    initialValue?.tenantAccess.tenantIds.join(', ') ?? '',
  )
  const [minSizeGb, setMinSizeGb] = useState(
    initialValue?.sizeLimits?.minSizeGb?.toString() ?? '',
  )
  const [maxSizeGb, setMaxSizeGb] = useState(
    initialValue?.sizeLimits?.maxSizeGb?.toString() ?? '',
  )
  const [validationError, setValidationError] = useState('')

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()

    const tenantIds = parseList(tenantIdsText)
    const minSize = minSizeGb.trim() ? Number(minSizeGb) : null
    const maxSize = maxSizeGb.trim() ? Number(maxSizeGb) : null

    if (!displayName.trim()) {
      setValidationError('Display name is required.')
      return
    }

    if (!storageClassName.trim()) {
      setValidationError('A storage class is required for this target.')
      return
    }

    if (allowedResourceTypes.length === 0) {
      setValidationError('At least one resource type is required.')
      return
    }

    if (tenantAccessMode === 'SelectedTenants' && tenantIds.length === 0) {
      setValidationError('At least one tenant id is required for selected tenants.')
      return
    }

    if ((minSize !== null && minSize <= 0) || (maxSize !== null && maxSize <= 0)) {
      setValidationError('Size limits must be greater than zero.')
      return
    }

    if (minSize !== null && maxSize !== null && minSize > maxSize) {
      setValidationError('Minimum size must be less than or equal to maximum size.')
      return
    }

    setValidationError('')

    const mapping = { deploymentTargetId, storageClassName: storageClassName.trim() }
    const targetMappings = (initialValue?.targetMappings ?? []).map((current) =>
      current.deploymentTargetId === deploymentTargetId ? mapping : current)
    if (!targetMappings.some((current) => current.deploymentTargetId === deploymentTargetId)) targetMappings.push(mapping)

    try {
      await onSubmit({
        displayName: displayName.trim(),
        description: description.trim(),
        mediaType,
        performanceTier,
        isEnabled,
        isDefault,
        targetMappings,
        ...(initialValue ? { revision: initialValue.revision } : {}),
        allowedResourceTypes,
        tenantAccess: {
          mode: tenantAccessMode,
          tenantIds: tenantAccessMode === 'SelectedTenants' ? tenantIds : [],
        },
        sizeLimits:
          minSize !== null || maxSize !== null
            ? {
                minSizeGb: minSize,
                maxSizeGb: maxSize,
              }
            : null,
      })
    } catch { return }

    if (mode === 'create') {
      setDisplayName('')
      setDescription('')
      setIsDefault(false)
      setTenantAccessMode('AllTenants')
      setTenantIdsText('')
      setMinSizeGb('')
      setMaxSizeGb('')
    }
  }

  return (
    <section className="panel web-service-form-panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">{mode === 'create' ? 'Create' : 'Edit'}</p>
          <h2 className="panel__title">
            {mode === 'create' ? 'New storage profile' : 'Edit storage profile'}
          </h2>
        </div>
      </div>

      <form className="resource-group-form" onChange={() => onDirtyChange(true)} onSubmit={(event) => void handleSubmit(event)}>
        <Field label="Display name">
          <input
            className="field__control"
            value={displayName}
            onChange={(event) => setDisplayName(event.target.value)}
            maxLength={80}
            placeholder="Default SSD"
          />
        </Field>

        <Field label="Description">
          <textarea
            className="field__control field__control--textarea"
            value={description}
            onChange={(event) => setDescription(event.target.value)}
            maxLength={500}
            placeholder="General-purpose profile for tenant data services."
          />
        </Field>

        <div className="form-grid form-grid--three">
          <Field label="Media">
            <select
              className="field__control"
              value={mediaType}
              onChange={(event) => setMediaType(event.target.value as StorageMediaType)}
            >
              {mediaTypes.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Tier">
            <select
              className="field__control"
              value={performanceTier}
              onChange={(event) => setPerformanceTier(event.target.value as StoragePerformanceTier)}
            >
              {performanceTiers.map((item) => (
                <option key={item} value={item}>
                  {item}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Tenant access">
            <select
              className="field__control"
              value={tenantAccessMode}
              onChange={(event) =>
                setTenantAccessMode(event.target.value as StorageProfileTenantAccessMode)
              }
            >
              <option value="AllTenants">All tenants</option>
              <option value="SelectedTenants">Selected tenants</option>
            </select>
          </Field>
        </div>

        <div className="toggle-grid">
          <label className="toggle-row">
            <input
              checked={isEnabled}
              onChange={(event) => setIsEnabled(event.target.checked)}
              type="checkbox"
            />
            <span>Enabled</span>
          </label>
          <label className="toggle-row">
            <input
              checked={isDefault}
              onChange={(event) => setIsDefault(event.target.checked)}
              type="checkbox"
            />
            <span>Default profile</span>
          </label>
        </div>

        <Field label="Storage class">
          {storageClasses.length > 0 ? (
            <select className="field__control" value={storageClassName} onChange={(event) => setStorageClassName(event.target.value)}>
              <option value="">Choose a storage class</option>
              {storageClassName && !storageClasses.some((item) => item.name === storageClassName) && <option value={storageClassName}>{storageClassName} · Unavailable</option>}
              {storageClasses.map((item) => (
                <option key={item.name} value={item.name}>{item.name}{item.isDefault ? ' · Default' : ''}</option>
              ))}
            </select>
          ) : (
            <input
              className="field__control"
              value={storageClassName}
              onChange={(event) => setStorageClassName(event.target.value)}
              placeholder="standard"
            />
          )}
        </Field>

        <div className="field">
          <span className="field__label">Allowed resource types</span>
          <div className="toggle-grid">
            {resourceTypes.map((item) => (
              <label className="toggle-row" key={item}>
                <input
                  checked={allowedResourceTypes.includes(item)}
                  onChange={() =>
                    setAllowedResourceTypes((current) => toggleValue(current, item))
                  }
                  type="checkbox"
                />
                <span>{item}</span>
              </label>
            ))}
          </div>
        </div>

        {tenantAccessMode === 'SelectedTenants' && (
          <Field label="Tenant ids" hint="Separate tenant ids with commas.">
            <input
              className="field__control"
              value={tenantIdsText}
              onChange={(event) => setTenantIdsText(event.target.value)}
              placeholder="tenant-a, tenant-b"
            />
          </Field>
        )}

        <div className="form-grid form-grid--three">
          <Field label="Minimum GB">
            <input
              className="field__control"
              min="1"
              value={minSizeGb}
              onChange={(event) => setMinSizeGb(event.target.value)}
              type="number"
            />
          </Field>
          <Field label="Maximum GB">
            <input
              className="field__control"
              min="1"
              value={maxSizeGb}
              onChange={(event) => setMaxSizeGb(event.target.value)}
              type="number"
            />
          </Field>
        </div>

        {(validationError || submitError) && (
          <div className="form-message form-message--error">
            {validationError || submitError}
          </div>
        )}

        <div className="resource-group-form__actions">
          <button
            className="button button--primary"
            disabled={submitStatus === 'loading'}
            type="submit"
          >
            {submitStatus === 'loading'
              ? 'Saving...'
              : mode === 'create'
                ? 'Create profile'
                : 'Save changes'}
          </button>
          {mode === 'edit' && onCancelEdit && (
            <button className="button" onClick={onCancelEdit} type="button">
              Cancel
            </button>
          )}
        </div>
      </form>
    </section>
  )
}
