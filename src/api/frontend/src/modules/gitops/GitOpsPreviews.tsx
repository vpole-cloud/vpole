// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useState } from 'react'
import { ApiError } from '../../api/transport'
import { useAppServices } from '../../shell/AppContext'
import { Button, DataTable, EmptyState, Notice } from '../../ui/components'
import { createPreview, fetchPreviews, safeGitOpsError } from './api'
import { LifecycleForm } from './LifecycleForm'
import { initialLifecycleDraft, type LifecycleDraft } from './lifecycleModel'
import { previewActivityAllowed } from './previewModel'
import { referenceParams, selectedReference } from './navigation'
import { PageControls } from './PageControls'
import { PreviewReview } from './PreviewReview'
import type { GitOpsSource, PreviewRequest } from './types'
import { useGitOpsMutation, useGitOpsQuery } from './useGitOpsQuery'

export function GitOpsPreviews({
  source,
  active,
  healthy,
  onRun,
}: {
  source: GitOpsSource
  active: boolean
  healthy: boolean
  onRun: (runId: string) => void
}) {
  const { tenant, navigation } = useAppServices()
  const [page, setPage] = useState(1)
  const history = useGitOpsQuery(['previews', source.id, page], active, 20000, (api, signal) =>
    fetchPreviews(api, { sourceId: source.id, page }, signal),
  )
  const selected = selectedReference(navigation.search, source.id, 'preview')
  const formKey = `${history.origin.identity}:${source.id}:${tenant.canManage}:${active}`
  const [draftIdentity, setDraftIdentity] = useState(formKey)
  const [draft, setDraft] = useState<LifecycleDraft>(() =>
    initialLifecycleDraft(source.activity === 'Detaching' ? 'Detach' : 'ResourceChanges', source),
  )
  if (draftIdentity !== formKey) {
    setDraftIdentity(formKey)
    setDraft(
      initialLifecycleDraft(source.activity === 'Detaching' ? 'Detach' : 'ResourceChanges', source),
    )
  }
  return (
    <section aria-label="GitOps previews">
      <div className="panel__header">
        <h2>Lifecycle previews</h2>
        <Button onClick={() => void history.refresh()}>Refresh preview history</Button>
      </div>
      {history.error && <Notice tone="error">{history.error}</Notice>}
      {history.resource ? (
        <>
          <DataTable
            label="Lifecycle preview history"
            items={history.resource.items}
            itemKey={(item) => item.id}
            rowTestId={(item) => `gitops-preview-row-${item.id}`}
            columns={[
              {
                id: 'preview',
                label: 'Preview',
                render: (item) => (
                  <Button
                    onClick={() =>
                      navigation.setSearch(
                        referenceParams(navigation.search, source.id, 'preview', item.id),
                      )
                    }
                  >
                    {item.id}
                  </Button>
                ),
              },
              { id: 'intent', label: 'Intent', render: (item) => item.intent },
              { id: 'state', label: 'State', render: (item) => item.state },
              { id: 'commit', label: 'Commit', render: (item) => item.commit ?? 'Inventory only' },
              { id: 'created', label: 'Created', render: (item) => item.createdAtUtc },
            ]}
          />
          <PageControls label="previews" page={page} value={history.resource} onPage={setPage} />
        </>
      ) : (
        <EmptyState title="Loading preview history…" />
      )}
      {selected && (
        <PreviewReview
          key={`${formKey}:${selected}`}
          source={source}
          previewId={selected}
          active={active}
          healthy={healthy}
          onRun={onRun}
          onNewPreview={() => {
            const params = new URLSearchParams(navigation.search)
            params.delete('preview')
            navigation.setSearch(params)
          }}
        />
      )}
      {!selected && (
        <NewPreview
          key={formKey}
          source={source}
          active={active}
          healthy={healthy}
          draft={draft}
          onDraft={setDraft}
          onCreated={(id) =>
            navigation.setSearch(referenceParams(navigation.search, source.id, 'preview', id))
          }
          onInspect={() => void history.refresh()}
        />
      )}
    </section>
  )
}
function NewPreview({
  source,
  active,
  healthy,
  onCreated,
  onInspect,
  draft,
  onDraft,
}: {
  source: GitOpsSource
  active: boolean
  healthy: boolean
  onCreated: (id: string) => void
  onInspect: () => void
  draft: LifecycleDraft
  onDraft: (draft: LifecycleDraft) => void
}) {
  const { tenant } = useAppServices()
  const mutation = useGitOpsMutation(`preview-create:${source.id}`, tenant.canManage && active)
  const [error, setError] = useState<ApiError | null>(null)
  const [unknown, setUnknown] = useState(false)
  const allowed =
    tenant.canManage &&
    active &&
    healthy &&
    (source.activity === 'Active' ||
      source.activity === 'Paused' ||
      source.activity === 'Detaching')
  async function submit(request: PreviewRequest) {
    if (
      !allowed ||
      mutation.busy ||
      unknown ||
      request.expectedRevision !== source.revision ||
      !previewActivityAllowed(source, request.intent)
    )
      return
    const command = structuredClone(request)
    setError(null)
    try {
      await mutation.run(
        (api, signal) => createPreview(api, { sourceId: source.id, request: command }, signal),
        (result, guard) => {
          guard()
          onCreated(result.id)
        },
      )
    } catch (failure) {
      if (failure instanceof DOMException && failure.name === 'AbortError') return
      const value = safeGitOpsError(failure)
      setError(value)
      if (value.status === 0 || value.status >= 500) setUnknown(true)
    }
  }
  return (
    <LifecycleForm
      source={source}
      active={active}
      draft={draft}
      onChange={onDraft}
      allowed={allowed}
      busy={mutation.busy}
      error={error}
      unknown={unknown}
      onSubmit={(request) => void submit(request)}
      onInspect={onInspect}
    />
  )
}
