// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useState } from 'react'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { DataTable, EmptyState, Notice } from '../../ui/components'
import { fetchResources } from './api'
import { resourceIdentity } from './lifecycleModel'
import { safeCode } from './model'
import { PageControls } from './PageControls'
import type { GitOpsSource, ResourceIdentity } from './types'
import { useGitOpsQuery } from './useGitOpsQuery'
export function InventorySelection({
  source,
  active,
  disabled,
  selected,
  onChange,
}: {
  source: GitOpsSource
  active: boolean
  disabled: boolean
  selected: ResourceIdentity[]
  onChange: (resources: ResourceIdentity[]) => void
}) {
  const [page, setPage] = useState(1)
  const inventory = useGitOpsQuery(['resources', source.id, page], active, 20000, (api, signal) =>
    fetchResources(api, { sourceId: source.id, page }, signal),
  )
  const current =
    hasHealthyCurrentQueryData(inventory) &&
    inventory.resource.inventoryRevision === source.inventory.revision
  return (
    <section aria-label="Select owned resources">
      <h4>Owned resources</h4>
      <p>
        Selections use concrete IDs. Remove the corresponding declarations in Git before requesting
        removal; the server checks the complete snapshot.
      </p>
      {inventory.error && <Notice tone="error">{inventory.error}</Notice>}
      {inventory.resource && !current && (
        <Notice>
          Inventory changed. Wait for current source metadata and review the selected IDs.
        </Notice>
      )}
      {inventory.resource ? (
        <>
          <DataTable
            label="Owned resource selection"
            items={inventory.resource.items}
            itemKey={(item) => item.resourceId}
            rowTestId={(item) => `gitops-owned-selection-${item.resourceId}`}
            columns={[
              {
                id: 'select',
                label: 'Select',
                render: (item) => (
                  <input
                    type="checkbox"
                    aria-label={`Select ${item.kind}/${item.name}`}
                    checked={selected.some((value) => value.resourceId === item.resourceId)}
                    disabled={
                      disabled ||
                      !current ||
                      (selected.length >= 256 &&
                        !selected.some((value) => value.resourceId === item.resourceId))
                    }
                    onChange={(event) =>
                      onChange(
                        event.target.checked
                          ? [...selected, resourceIdentity(item)]
                          : selected.filter((value) => value.resourceId !== item.resourceId),
                      )
                    }
                  />
                ),
              },
              {
                id: 'resource',
                label: 'Resource',
                render: (item) => (
                  <>
                    {item.kind}/{item.name}
                    <p>{item.resourceId}</p>
                  </>
                ),
              },
              {
                id: 'identity',
                label: 'Identity',
                render: (item) =>
                  item.materialization === 'Allocated' ? 'Reserved ID' : 'Previously created',
              },
              {
                id: 'removal',
                label: 'Removal',
                render: (item) => (item.pendingRemoval ? 'Pending removal' : 'Declared'),
              },
              {
                id: 'blocker',
                label: 'Blocker',
                render: (item) => (item.blocker ? safeCode(item.blocker) : 'None'),
              },
            ]}
          />
          <PageControls
            label="owned resources"
            page={page}
            value={inventory.resource}
            onPage={setPage}
          />
        </>
      ) : (
        <EmptyState title="Loading owned resources…" />
      )}
    </section>
  )
}
