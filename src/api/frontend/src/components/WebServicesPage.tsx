import { useState } from 'react'
import { WebServiceForm } from './WebServiceForm'
import { SourceBuildPanel } from './SourceBuildPanel'
import type { SourceBuildsManager } from '../hooks/useSourceBuildsManager'
import type {
  LoadStatus,
  ResourceGroup,
  WebService,
  WebServiceInput,
} from '../types'

interface WebServicesPageProps {
  items: WebService[]
  resourceGroups: ResourceGroup[]
  status: LoadStatus
  error?: string
  submitStatus: LoadStatus
  submitError?: string
  deletingId: string | null
  onRefresh: () => Promise<void>
  onCreate: (input: WebServiceInput) => Promise<void>
  onUpdate: (id: string, input: WebServiceInput) => Promise<void>
  onDelete: (id: string) => Promise<void>
  selectedId: string | null
  onSelect: (id: string | null) => void
  sourceBuilds: SourceBuildsManager
  canManage?: boolean
}

export function WebServicesPage({
  items,
  resourceGroups,
  status,
  error,
  submitStatus,
  submitError,
  deletingId,
  onRefresh,
  onCreate,
  onUpdate,
  onDelete,
  selectedId,
  onSelect,
  sourceBuilds,
  canManage = true,
}: WebServicesPageProps) {
  const [query, setQuery] = useState('')

  const filteredItems = items.filter((item) =>
    [
      item.name,
      item.dnsRecordShortInfo?.domain ?? '',
      item.spec.image.server,
      item.spec.image.name,
    ]
      .join(' ')
      .toLowerCase()
      .includes(query.trim().toLowerCase()),
  )

  const selectedService =
    filteredItems.find((item) => item.id === selectedId) ??
    items.find((item) => item.id === selectedId) ??
    null

  const resolveResourceGroupName = (resourceGroupId: string) =>
    resourceGroups.find((group) => group.id === resourceGroupId)?.name ?? 'Unknown group'

  const handleDelete = async (item: WebService) => {
    const confirmed = window.confirm(`Delete web service "${item.name}"?`)

    if (!confirmed) {
      return
    }

    await onDelete(item.id)

    if (selectedId === item.id) {
      onSelect(null)
    }
  }

  return (
    <div className="resource-groups-page">
      <section className="hero-panel">
        <div className="hero-panel__main">
          <p className="hero-panel__eyebrow">Web services</p>
          <h1 className="hero-panel__title">Application runtime</h1>
          <p className="hero-panel__description">
            Manage deployed applications, image versions, runtime resources, and health settings from one operator workspace.
          </p>
        </div>

        <aside className="hero-panel__utility">
          <div className="session-card">
            <div>
              <p className="session-card__label">Workspace tools</p>
              <p className="session-card__value">Manage services</p>
            </div>
            <div className="session-card__actions">
              <button className="button" onClick={() => void onRefresh()} type="button">
                Refresh
              </button>
              <button className="button" disabled={!canManage} onClick={() => onSelect(null)} type="button">
                New service
              </button>
            </div>
            <p className="session-card__note">
              Create new deployments or select an existing service to update its runtime settings.
            </p>
          </div>
        </aside>
      </section>

      <div className="resource-groups-layout resource-groups-layout--wide-form">
        <section className="panel resource-groups-list-panel">
          <div className="panel__header">
            <div>
              <p className="panel__eyebrow">Runtime</p>
              <h2 className="panel__title">All web services</h2>
            </div>
            <span className="panel__meta">{filteredItems.length} visible</span>
          </div>

          <label className="field">
            <span className="field__label">Search</span>
            <input
              className="field__control"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Find by name, domain, or image"
            />
          </label>

          {status === 'loading' && (
            <div className="empty-state">Loading web services...</div>
          )}

          {status === 'error' && (
            <div className="empty-state empty-state--error">
              {error || 'Unable to load web services.'}
            </div>
          )}

          {status === 'success' && filteredItems.length === 0 && (
            <div className="empty-state">
              {items.length === 0
                ? 'No web services exist yet. Create the first one in the side panel.'
                : 'No web services match this search.'}
            </div>
          )}

          {status === 'success' && filteredItems.length > 0 && (
            <div className="list">
              {filteredItems.map((item) => {
                const image = `${item.spec.image.server}/${item.spec.image.name}:${item.spec.image.tag}`
                const isDeleting = deletingId === item.id

                return (
                  <article
                    className={`list-row list-row--interactive${selectedId === item.id ? ' is-selected' : ''}`}
                    key={item.id}
                  >
                    <button
                      className="list-row__button"
                      onClick={() => onSelect(item.id)}
                      type="button"
                    >
                      <div className="list-row__primary">
                        <h3>{item.name}</h3>
                        <p>{item.dnsRecordShortInfo?.domain || 'No public domain assigned yet.'}</p>
                      </div>
                      <div className="list-row__secondary">
                        <span className="status-pill status-pill--accent">
                          {item.spec.replicas} replicas
                        </span>
                        <span className="list-row__meta">
                          {resolveResourceGroupName(item.resourceGroupId)} · {image}
                        </span>
                      </div>
                    </button>

                    <div className="row-actions">
                      <button className="button" onClick={() => onSelect(item.id)} type="button">
                        Edit
                      </button>
                      <button
                        className="button button--danger"
                        disabled={isDeleting || !canManage}
                        onClick={() => void handleDelete(item)}
                        type="button"
                      >
                        {isDeleting ? 'Deleting...' : 'Delete'}
                      </button>
                    </div>
                  </article>
                )
              })}
            </div>
          )}
        </section>

        {canManage ? (
          <WebServiceForm
            key={selectedService?.id ?? 'create'}
            mode={selectedService ? 'edit' : 'create'}
            resourceGroups={resourceGroups}
            initialValue={selectedService}
            submitStatus={submitStatus}
            submitError={submitError}
            onSubmit={async (input) => {
              if (selectedService) {
                await onUpdate(selectedService.id, input)
                return
              }

              await onCreate(input)
            }}
            onCancelEdit={selectedService ? () => onSelect(null) : undefined}
          />
        ) : (
          <section className="panel">
            <div className="empty-state">Tenant viewer access is read-only.</div>
          </section>
        )}
      </div>
      {selectedService && (
        <SourceBuildPanel
          key={selectedService.id}
          webService={selectedService}
          manager={sourceBuilds}
          canManage={canManage}
        />
      )}
    </div>
  )
}
