// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { LoadStatus, ResourceGroup } from '../types'

interface ResourceGroupListProps {
  items: ResourceGroup[]
  status: LoadStatus
  error?: string
}

export function ResourceGroupList({ items, status, error }: ResourceGroupListProps) {
  return (
    <section className="panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">Inventory</p>
          <h2 className="panel__title">Resource groups</h2>
        </div>
        <span className="panel__meta">{items.length} visible</span>
      </div>

      {status === 'loading' && <div className="empty-state">Loading resource groups...</div>}

      {status === 'error' && (
        <div className="empty-state empty-state--error">
          {error || 'Unable to load resource groups.'}
        </div>
      )}

      {status === 'success' && items.length === 0 && (
        <div className="empty-state">
          No resource groups yet. This section is ready for your first environment boundary.
        </div>
      )}

      {status === 'success' && items.length > 0 && (
        <div className="list">
          {items.map((item) => (
            <article className="list-row" key={item.id}>
              <div className="list-row__primary">
                <h3>{item.name}</h3>
                <p>{item.description || 'No description provided yet.'}</p>
              </div>
              <div className="list-row__secondary">
                <span className="status-pill">{item.resourceCount} resources</span>
                <span className="list-row__meta">
                  {item.tags.length > 0 ? item.tags.join(', ') : 'No tags'}
                </span>
              </div>
            </article>
          ))}
        </div>
      )}
    </section>
  )
}
