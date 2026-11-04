import { gitOpsSourceResourceType } from '../../data/gitOps'
import { useState } from 'react'
import type { GitOpsSource, Page } from './types'
import { useAppServices } from '../../shell/AppContext'
import { Button, DataTable, EmptyState, Notice, StatusBadge } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { fetchSources } from './api'
import { PageControls } from './PageControls'
import { sourceExecuting, sourceLabel, type GitOpsSection } from './model'
import { useGitOpsQuery } from './useGitOpsQuery'
export function GitOpsCatalog({
  active,
  actions,
}: {
  active: boolean
  actions: WorkspaceActions<GitOpsSection>
}) {
  const { tenant, navigation } = useAppServices()
  const [page, setPage] = useState(1)
  const query = useGitOpsQuery<Page<GitOpsSource>>(
    ['sources', page],
    active,
    (value) => (value?.items.some(sourceExecuting) ? 5000 : 20000),
    (api, signal) => fetchSources(api, page, signal),
  )
  return (
    <section className="panel">
      <header className="ui-page-header">
        <div>
          <p className="ui-page-eyebrow">Delivery</p>
          <h1>GitOps sources</h1>
          <p>Repository configuration, execution and reviewed lifecycle.</p>
        </div>
        <div className="row-actions">
          <Button onClick={() => void query.refresh()}>Refresh sources</Button>
          {tenant.canManage && (
            <Button variant="primary" onClick={() => actions.open('create')}>
              New source
            </Button>
          )}
        </div>
      </header>
      {query.error && (
        <Notice tone="error">{query.error} Previously loaded rows remain visible.</Notice>
      )}
      {!query.resource && (
        <EmptyState
          title={query.status === 'error' ? 'Sources unavailable.' : 'Loading sources…'}
        />
      )}
      {query.resource?.items.length === 0 && <EmptyState title="No GitOps sources yet." />}
      {query.resource && (
        <DataTable
          label="GitOps sources"
          items={query.resource.items}
          itemKey={(item) => item.id}
          rowTestId={(item) => `resource-row-${item.id}`}
          columns={[
            {
              id: 'source',
              label: 'Source',
              render: (source) => (
                <a
                  href={navigation.resourceHref({
                    resourceTypeId: gitOpsSourceResourceType,
                    resourceId: source.id,
                  })}
                  onClick={(event) => {
                    if (event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
                      event.preventDefault()
                      actions.loaded(source.id, sourceLabel(source))
                      actions.open(source.id)
                    }
                  }}
                >
                  {sourceLabel(source)}
                </a>
              ),
            },
            { id: 'activity', label: 'Activity', render: (source) => source.activity },
            {
              id: 'status',
              label: 'Execution',
              render: (source) => <StatusBadge>{source.status}</StatusBadge>,
            },
            {
              id: 'commits',
              label: 'Fully applied commit',
              render: (source) => source.synchronization.fullyAppliedCommit ?? 'Not applied',
            },
            {
              id: 'inventory',
              label: 'Owned / pending removal',
              render: (source) => `${source.inventory.owned} / ${source.inventory.pendingRemoval}`,
            },
          ]}
        />
      )}
      <PageControls label="sources" page={page} value={query.resource} onPage={setPage} />
    </section>
  )
}
