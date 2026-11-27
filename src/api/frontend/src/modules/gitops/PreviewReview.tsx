// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useState } from 'react'
import { ApiError } from '../../api/transport'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { useAppServices } from '../../shell/AppContext'
import { Button, ConfirmationDialog, DataTable, EmptyState, Notice } from '../../ui/components'
import { confirmPreview, safeGitOpsError } from './api'
import { PageControls } from './PageControls'
import {
  approvalMatchesSource,
  needsDataLossAcknowledgment,
  previewActivityAllowed,
  previewStamp,
  warningDescription,
} from './previewModel'
import type { GitOpsPreview, GitOpsSource } from './types'
import { useGitOpsMutation } from './useGitOpsQuery'
import { usePreviewReview } from './usePreviewReview'

export function PreviewSummary({ preview }: { preview: GitOpsPreview }) {
  const summary = preview.summary
  return (
    <div>
      <p>
        {summary.total} plan actions: {summary.apply} apply, {summary.adopt} adopt, {summary.remove}{' '}
        remove, {summary.replace} replace, {summary.retain} retain, {summary.detach} detach.
      </p>
      <ul>
        {summary.warnings.map((code) => (
          <li key={code}>{warningDescription(code)}</li>
        ))}
      </ul>
    </div>
  )
}

export function PreviewReview({
  source,
  previewId,
  active,
  healthy,
  onRun,
  onNewPreview,
}: {
  source: GitOpsSource
  previewId: string
  active: boolean
  healthy: boolean
  onRun: (runId: string) => void
  onNewPreview: () => void
}) {
  const { tenant } = useAppServices()
  const query = usePreviewReview(source.id, previewId, active)
  const mutation = useGitOpsMutation(
    `preview-confirm:${source.id}:${previewId}`,
    tenant.canManage && active,
  )
  const [page, setPage] = useState(1)
  const [dialog, setDialog] = useState<string | null>(null)
  const [acknowledged, setAcknowledged] = useState<string | null>(null)
  const [acceptedRun, setAcceptedRun] = useState<string | null>(null)
  const [staleStamp, setStaleStamp] = useState<string | null>(null)
  const [failure, setFailure] = useState<ApiError | null>(null)
  const [unknown, setUnknown] = useState<{ previewId: string; expectedRevision: number } | null>(
    null,
  )
  const reviewed = query.resource
  const preview = reviewed?.preview
  const stamp = preview ? previewStamp(preview) : ''
  const acknowledgmentKey = preview ? `${preview.id}:${preview.revision}` : ''
  const current = preview && approvalMatchesSource(source, preview)
  const allowed = Boolean(
    preview &&
    healthy &&
    hasHealthyCurrentQueryData(query) &&
    current &&
    preview.state === 'AwaitingConfirmation' &&
    tenant.canManage &&
    active &&
    previewActivityAllowed(source, preview.intent) &&
    !acceptedRun &&
    staleStamp !== stamp,
  )
  const identity = `${source.id}:${previewId}:${stamp}:${tenant.canManage}:${active}`
  const [formIdentity, setFormIdentity] = useState(identity)
  if (formIdentity !== identity) {
    setFormIdentity(identity)
    setDialog(null)
    setAcknowledged(null)
    setUnknown(null)
    setFailure(null)
  }
  const currentMetadata = Boolean(healthy && query.status !== 'error' && current)
  const [metadataHealthy, setMetadataHealthy] = useState(currentMetadata)
  if (metadataHealthy !== currentMetadata) {
    setMetadataHealthy(currentMetadata)
    if (!currentMetadata) {
      setDialog(null)
      setAcknowledged(null)
    }
  }
  const showDialog = dialog === stamp && allowed
  async function confirm() {
    if (
      !preview ||
      !allowed ||
      dialog !== stamp ||
      (needsDataLossAcknowledgment(preview) && acknowledged !== acknowledgmentKey)
    )
      return
    const command = unknown ?? { previewId: preview.id, expectedRevision: preview.revision }
    if (command.previewId !== preview.id || command.expectedRevision !== preview.revision) return
    setFailure(null)
    try {
      await mutation.run(
        (api, signal) => confirmPreview(api, { sourceId: source.id, ...command }, signal),
        (result, guard) => {
          guard()
          setAcceptedRun(result.runId)
          setUnknown(null)
          setDialog(null)
          onRun(result.runId)
        },
      )
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') return
      const value = safeGitOpsError(error)
      setFailure(value)
      if (value.status === 0 || value.status >= 500) setUnknown(command)
      else setUnknown(null)
      if (value.code === 'gitops.preview-stale') {
        setStaleStamp(stamp)
        setDialog(null)
        setAcknowledged(null)
      }
    }
  }
  if (!reviewed || !preview)
    return (
      <section>
        <EmptyState
          title={query.error ? 'Complete preview unavailable.' : 'Loading complete preview…'}
          error={Boolean(query.error)}
        />
        {query.error && <Notice tone="error">{query.error}</Notice>}
        <Button onClick={() => void query.refresh()}>Refresh complete preview</Button>
      </section>
    )
  const runId = acceptedRun ?? preview.runId
  return (
    <section aria-label="Lifecycle preview review" data-testid={`gitops-preview-${preview.id}`}>
      <header className="panel__header">
        <h3>Review {preview.intent} preview</h3>
        <Button onClick={() => void query.refresh()} disabled={mutation.busy}>
          Refresh complete preview
        </Button>
      </header>
      <p>
        Preview {preview.id} · revision {preview.revision} · source revision{' '}
        {preview.sourceRevision} · inventory revision {preview.inventoryRevision}
      </p>
      <p>Commit: {preview.commit ?? 'Inventory only; repository access is not required.'}</p>
      {preview.input.kind === 'Repository' && (
        <div>
          <p>
            Repository: {preview.input.repository.provider} /{' '}
            {preview.input.repository.connectionId} /{' '}
            {preview.input.repository.externalRepositoryId}
          </p>
          <p>
            Branch: {preview.input.repository.branch} · directory:{' '}
            {preview.input.repository.manifestDirectory || '(root)'}
          </p>
          <p>Desired hash: {preview.input.desiredHash}</p>
        </div>
      )}
      <PreviewSummary preview={preview} />
      {!current && (
        <Notice tone="error">Source or inventory changed. Build and review a new preview.</Notice>
      )}
      {query.error && (
        <Notice tone="error">
          {query.error} Confirmation stays disabled until a successful refresh.
        </Notice>
      )}
      {failure && <Notice tone="error">{failure.message}</Notice>}
      {unknown && (
        <Notice>
          The confirmation outcome is unknown. Refresh this preview to find an accepted run, or
          explicitly repeat the exact same confirmation.
        </Notice>
      )}
      <DataTable
        label="Lifecycle plan actions"
        items={reviewed.actions.slice((page - 1) * 20, page * 20)}
        itemKey={(action) => action.id}
        rowTestId={(action) => `gitops-action-${action.id}`}
        columns={[
          { id: 'action', label: 'Action', render: (action) => action.action },
          {
            id: 'resource',
            label: 'Resource',
            render: (action) => (
              <>
                {action.resource.kind}/{action.resource.name}
                <p>{action.resource.resourceId}</p>
              </>
            ),
          },
          {
            id: 'replacement',
            label: 'New identity',
            render: (action) =>
              action.replacement ? (
                <>
                  {action.replacement.kind}/{action.replacement.name}
                  <p>{action.replacement.resourceId}</p>
                </>
              ) : (
                '—'
              ),
          },
          {
            id: 'fields',
            label: 'Changed fields',
            render: (action) => action.changedFields.join(', ') || 'None',
          },
          {
            id: 'dependencies',
            label: 'Dependencies',
            render: (action) => action.dependencies.join(', ') || 'None',
          },
          {
            id: 'warnings',
            label: 'Warnings',
            render: (action) => action.warnings.map(warningDescription).join(' ') || 'None',
          },
        ]}
      />
      <PageControls
        label="plan actions"
        page={page}
        value={{ hasMore: page * 20 < reviewed.actions.length }}
        onPage={setPage}
      />
      <div className="row-actions">
        <Button onClick={onNewPreview} disabled={!tenant.canManage || !active || mutation.busy}>
          Build a new preview
        </Button>
        {runId ? (
          <Button
            disabled={!active}
            onClick={() => {
              if (active && query.origin.isCurrent()) onRun(runId)
            }}
          >
            Open accepted run
          </Button>
        ) : (
          <Button
            variant="danger"
            onClick={() => {
              setDialog(stamp)
              setFailure(null)
            }}
            disabled={!allowed || mutation.busy}
          >
            Review confirmation
          </Button>
        )}
      </div>
      {showDialog && (
        <ConfirmationDialog
          title="Confirm the reviewed lifecycle plan?"
          confirmLabel={unknown ? 'Repeat exact confirmation' : 'Confirm lifecycle plan'}
          busy={mutation.busy}
          confirmDisabled={
            !allowed || (needsDataLossAcknowledgment(preview) && acknowledged !== acknowledgmentKey)
          }
          onCancel={() => setDialog(null)}
          onConfirm={() => void confirm()}
        >
          <PreviewSummary preview={preview} />
          <p>
            Confirm preview {preview.id}, revision {preview.revision}, for source {source.id}. The
            server rechecks the complete plan before execution.
          </p>
          {needsDataLossAcknowledgment(preview) && (
            <label>
              <input
                type="checkbox"
                checked={acknowledged === acknowledgmentKey}
                onChange={(event) =>
                  setAcknowledged(event.target.checked ? acknowledgmentKey : null)
                }
              />{' '}
              I understand this plan may permanently delete data.
            </label>
          )}
          {failure && <Notice tone="error">{failure.message}</Notice>}
        </ConfirmationDialog>
      )}
    </section>
  )
}
