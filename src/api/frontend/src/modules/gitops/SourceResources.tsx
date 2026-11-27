// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useState } from 'react'
import { useAppServices } from '../../shell/AppContext'
import { Button, DataTable, EmptyState, Notice } from '../../ui/components'
import { fetchResources } from './api'
import { useGitOpsQuery } from './useGitOpsQuery'
import { PageControls } from './PageControls'
import { safeCode, sourceInterval } from './model'
import type { GitOpsSource } from './types'
export function SourceResources({
  source,
  active,
  onRun,
}: {
  source: GitOpsSource
  active: boolean
  onRun: (id: string) => void
}) {
  const { navigation, resourceTypes } = useAppServices()
  const [page, setPage] = useState(1)
  const query = useGitOpsQuery(
    ['resources', source.id, page],
    active,
    sourceInterval(source),
    (api, signal) => fetchResources(api, { sourceId: source.id, page }, signal),
  )
  return (
    <section className="panel">
      <header className="panel__header">
        <h2>Owned resources</h2>
        <Button onClick={() => void query.refresh()}>Refresh resources</Button>
      </header>
      {query.error && <Notice tone="error">{query.error}</Notice>}
      {query.resource && query.resource.inventoryRevision !== source.inventory.revision && (
        <Notice>
          Inventory changed. Refresh source metadata before selecting lifecycle actions.
        </Notice>
      )}
      {!query.resource && <EmptyState title="Resource inventory unavailable or loading." />}
      {query.resource?.items.length === 0 && <EmptyState title="This source owns no resources." />}
      {query.resource && (
        <DataTable
          label="GitOps resources"
          items={query.resource.items}
          itemKey={(item) => item.resourceId}
          rowTestId={(item) => `gitops-resource-${item.resourceId}`}
          columns={[
            { id: 'kind', label: 'Kind', render: (item) => item.kind },
            {
              id: 'name',
              label: 'Resource',
              render: (item) =>
                item.resourceTypeId &&
                resourceTypes.some((type) => type.id === item.resourceTypeId) ? (
                  <a
                    href={navigation.resourceHref({
                      resourceTypeId: item.resourceTypeId,
                      resourceId: item.resourceId,
                    })}
                  >
                    {item.name}
                  </a>
                ) : (
                  item.name
                ),
            },
            {
              id: 'id',
              label: 'Identity',
              render: (item) => (
                <>
                  <code>{item.resourceId}</code>
                  <p>
                    {item.materialization === 'Allocated' ? 'Reserved ID' : 'Previously created'}
                  </p>
                </>
              ),
            },
            {
              id: 'removal',
              label: 'Ownership',
              render: (item) =>
                item.pendingRemoval ? 'Pending removal — ownership retained' : 'Git managed',
            },
            {
              id: 'blocker',
              label: 'Blocker',
              render: (item) => (item.blocker ? safeCode(item.blocker) : 'None recorded'),
            },
            {
              id: 'run',
              label: 'Last run',
              render: (item) => (
                <Button disabled={!item.lastRunId} onClick={() => onRun(item.lastRunId)}>
                  Open last run
                </Button>
              ),
            },
          ]}
        />
      )}
      <PageControls label="resources" page={page} value={query.resource} onPage={setPage} />
    </section>
  )
}
