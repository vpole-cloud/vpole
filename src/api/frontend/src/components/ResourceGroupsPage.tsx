import { useState } from 'react'
import { ResourceGroupForm } from './ResourceGroupForm'
import type { LoadStatus, ResourceGroup, ResourceGroupInput } from '../types'

interface ResourceGroupsPageProps {
  items: ResourceGroup[]
  status: LoadStatus
  error?: string
  submitStatus: LoadStatus
  submitError?: string
  deletingId: string | null
  onRefresh: () => Promise<void>
  onCreate: (input: ResourceGroupInput) => Promise<void>
  onUpdate: (id: string, input: ResourceGroupInput) => Promise<void>
  onDelete: (id: string) => Promise<void>
  canManage?: boolean
}

export function ResourceGroupsPage({
  items,
  status,
  error,
  submitStatus,
  submitError,
  deletingId,
  onRefresh,
  onCreate,
  onUpdate,
  onDelete,
  canManage = true,
}: ResourceGroupsPageProps) {
  const [query, setQuery] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)

  const filteredItems = items.filter((item) =>
    [item.name, item.description, item.tags.join(' ')]
      .join(' ')
      .toLowerCase()
      .includes(query.trim().toLowerCase()),
  )

  const selectedGroup =
    filteredItems.find((item) => item.id === selectedId) ??
    items.find((item) => item.id === selectedId) ??
    null

  const handleDelete = async (resourceGroup: ResourceGroup) => {
    const confirmed = window.confirm(
      `Delete resource group "${resourceGroup.name}"?`,
    )

    if (!confirmed) {
      return
    }

    await onDelete(resourceGroup.id)

    if (selectedId === resourceGroup.id) {
      setSelectedId(null)
    }
  }

  return (
    <div className="resource-groups-page">
      <section className="hero-panel">
        <div className="hero-panel__main">
          <p className="hero-panel__eyebrow">Resource groups</p>
          <h1 className="hero-panel__title">Environment boundaries</h1>
          <p className="hero-panel__description">
            Create, update, and retire the logical groups that back namespaces
            and future platform services.
          </p>
        </div>

        <aside className="hero-panel__utility">
          <div className="session-card">
            <div>
              <p className="session-card__label">Workspace tools</p>
              <p className="session-card__value">Manage groups</p>
            </div>

            <div className="session-card__actions">
              <button className="button" onClick={() => void onRefresh()} type="button">
                Refresh
              </button>
              <button
                className="button"
                disabled={!canManage}
                onClick={() => setSelectedId(null)}
                type="button"
              >
                New group
              </button>
            </div>

            <p className="session-card__note">
              Select a group to edit it, or keep the form in create mode for a
              new one.
            </p>
          </div>
        </aside>
      </section>

      <div className="resource-groups-layout">
        <section className="panel resource-groups-list-panel">
          <div className="panel__header">
            <div>
              <p className="panel__eyebrow">Inventory</p>
              <h2 className="panel__title">All resource groups</h2>
            </div>
            <span className="panel__meta">{filteredItems.length} visible</span>
          </div>

          <label className="field">
            <span className="field__label">Search</span>
            <input
              className="field__control"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Find by name, description, or tag"
            />
          </label>

          {status === 'loading' && (
            <div className="empty-state">Loading resource groups...</div>
          )}

          {status === 'error' && (
            <div className="empty-state empty-state--error">
              {error || 'Unable to load resource groups.'}
            </div>
          )}

          {status === 'success' && filteredItems.length === 0 && (
            <div className="empty-state">
              {items.length === 0
                ? 'No resource groups exist yet. Create the first one in the side panel.'
                : 'No resource groups match this search.'}
            </div>
          )}

          {status === 'success' && filteredItems.length > 0 && (
            <div className="list">
              {filteredItems.map((item) => {
                const isDeleting = deletingId === item.id

                return (
                  <article
                    className={`list-row list-row--interactive${selectedId === item.id ? ' is-selected' : ''}`}
                    key={item.id}
                  >
                    <button
                      className="list-row__button"
                      onClick={() => setSelectedId(item.id)}
                      type="button"
                    >
                      <div className="list-row__primary">
                        <h3>{item.name}</h3>
                        <p>{item.description || 'No description provided yet.'}</p>
                      </div>
                      <div className="list-row__secondary">
                        <span className="status-pill">
                          {item.resourceCount} resources
                        </span>
                        <span className="list-row__meta">
                          {item.tags.length > 0 ? item.tags.join(', ') : 'No tags'}
                        </span>
                      </div>
                    </button>

                    <div className="row-actions">
                      <button
                        className="button"
                        onClick={() => setSelectedId(item.id)}
                        type="button"
                      >
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
          <ResourceGroupForm
            key={selectedGroup?.id ?? 'create'}
            mode={selectedGroup ? 'edit' : 'create'}
            initialValue={selectedGroup}
            submitStatus={submitStatus}
            submitError={submitError}
            onSubmit={async (input) => {
              if (selectedGroup) {
                await onUpdate(selectedGroup.id, input)
                return
              }

              await onCreate(input)
            }}
            onCancelEdit={selectedGroup ? () => setSelectedId(null) : undefined}
          />
        ) : (
          <section className="panel">
            <div className="empty-state">Tenant viewer access is read-only.</div>
          </section>
        )}
      </div>
    </div>
  )
}
