// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useState } from 'react'
import { useAppServices } from '../../shell/AppContext'
import { Button, DataTable, EmptyState, Notice, StatusBadge } from '../../ui/components'
import { fetchRuns, fetchRun, fetchOperations } from './api'
import { useGitOpsQuery } from './useGitOpsQuery'
import { PageControls } from './PageControls'
import { sourceInterval, phaseLabel, safeCode, runExecuting } from './model'
import { selectedReference } from './navigation'
import type { GitOpsSource, GitOpsRun } from './types'
import { SourceCommands } from './SourceCommands'
export function SourceRuns({
  source,
  active,
  healthy,
  onRun,
  onDeleted,
  refreshSource,
}: {
  source: GitOpsSource
  active: boolean
  healthy: boolean
  onRun: (id: string) => void
  onDeleted: () => void
  refreshSource: () => void
}) {
  const { navigation } = useAppServices()
  const [page, setPage] = useState(1)
  const runId = selectedReference(navigation.search, source.id, 'run')
  const query = useGitOpsQuery(
    ['runs', source.id, page],
    active,
    sourceInterval(source),
    (api, signal) => fetchRuns(api, { sourceId: source.id, page }, signal),
  )
  return (
    <section className="panel">
      <h2>Run history</h2>
      <Button onClick={() => void query.refresh()}>Refresh runs</Button>
      {query.error && <Notice tone="error">{query.error}</Notice>}
      {query.resource?.items.length === 0 && <EmptyState title="No runs yet." />}
      {query.resource && (
        <DataTable
          label="GitOps runs"
          items={query.resource.items}
          itemKey={(item) => item.id}
          rowTestId={(item) => `gitops-run-${item.id}`}
          columns={[
            {
              id: 'id',
              label: 'Run',
              render: (item) => <Button onClick={() => onRun(item.id)}>{item.id}</Button>,
            },
            {
              id: 'trigger',
              label: 'Trigger / audit initiator',
              render: (item) => (
                <>
                  {item.trigger}
                  <p>{item.requestedByUserId ?? 'Server automation'}</p>
                </>
              ),
            },
            {
              id: 'status',
              label: 'Result',
              render: (item) => <StatusBadge>{item.status}</StatusBadge>,
            },
            {
              id: 'commit',
              label: 'Commit',
              render: (item) => <code>{item.commit ?? 'Not resolved'}</code>,
            },
            {
              id: 'generation',
              label: 'Generation / config revision',
              render: (item) => `${item.generation} / ${item.configurationRevision}`,
            },
            {
              id: 'retry',
              label: 'Retry of',
              render: (item) => item.retryOfRunId ?? 'Original run',
            },
          ]}
        />
      )}
      <PageControls label="runs" page={page} value={query.resource} onPage={setPage} />
      {runId && (
        <RunDetails
          key={runId}
          source={source}
          runId={runId}
          active={active}
          healthy={healthy}
          onDeleted={onDeleted}
          refreshSource={refreshSource}
        />
      )}
    </section>
  )
}
function RunDetails({
  source,
  runId,
  active,
  healthy,
  onDeleted,
  refreshSource,
}: {
  source: GitOpsSource
  runId: string
  active: boolean
  healthy: boolean
  onDeleted: () => void
  refreshSource: () => void
}) {
  const [page, setPage] = useState(1)
  const run = useGitOpsQuery<GitOpsRun>(
    ['run', source.id, runId],
    active,
    (value) => (value && runExecuting(value) ? 5000 : false),
    (api, signal) => fetchRun(api, { sourceId: source.id, runId }, signal),
  )
  const operations = useGitOpsQuery(
    ['operations', source.id, runId, page, run.resource?.status ?? null],
    active,
    run.resource && runExecuting(run.resource) ? 5000 : false,
    (api, signal) => fetchOperations(api, { sourceId: source.id, runId, page }, signal),
  )
  return (
    <article data-testid={`gitops-run-detail-${runId}`}>
      <h3>Run {runId}</h3>
      {run.error && <Notice tone="error">{run.error}</Notice>}
      {run.resource && (
        <>
          <p>
            Status: {run.resource.status} · commit {run.resource.commit ?? 'Not resolved'} ·
            generation {run.resource.generation}
          </p>
          <p>
            Trigger: {run.resource.trigger} · audit initiator:{' '}
            {run.resource.requestedByUserId ?? 'Server automation'} · Retry of:{' '}
            {run.resource.retryOfRunId ?? 'None'}
          </p>
          {run.resource.lifecycle && (
            <p>
              Lifecycle: {run.resource.lifecycle.authorization} · preview{' '}
              {run.resource.lifecycle.previewId}
            </p>
          )}
          {run.resource.operations && (
            <p>
              Create {run.resource.operations.create} · update {run.resource.operations.update} ·
              no-op {run.resource.operations.noOp} · blocked {run.resource.operations.blocked} ·
              pending {run.resource.operations.pending} operations.
            </p>
          )}
          {run.resource.operations?.lifecycle && (
            <p>
              Adopt {run.resource.operations.lifecycle.adopt} · remove{' '}
              {run.resource.operations.lifecycle.remove} · replace{' '}
              {run.resource.operations.lifecycle.replace} · retain{' '}
              {run.resource.operations.lifecycle.retain} · detach{' '}
              {run.resource.operations.lifecycle.detach} · pending cleanup{' '}
              {run.resource.operations.lifecycle.pendingCleanup} · awaiting confirmation{' '}
              {run.resource.operations.lifecycle.awaitingConfirmation}.
            </p>
          )}
          {run.resource.diagnostics.map((item, index) => (
            <Notice key={index} tone="error">
              {safeCode(item.code)}
              {item.file ? ` · ${item.file}` : ''}
              {item.document !== null ? ` · document ${item.document}` : ''}
              {item.field ? ` · ${item.field}` : ''}
            </Notice>
          ))}
          <SourceCommands
            source={source}
            run={run.resource}
            active={active}
            healthy={healthy && run.status !== 'error'}
            onDeleted={onDeleted}
            onAccepted={refreshSource}
            onlyRetry
          />
        </>
      )}
      <Button
        onClick={() => {
          void run.refresh()
          void operations.refresh()
        }}
      >
        Refresh run details
      </Button>
      {operations.error && <Notice tone="error">{operations.error}</Notice>}
      {operations.resource && (
        <DataTable
          label="Run operations"
          items={operations.resource.items}
          itemKey={(item) => `${item.kind}/${item.resourceId}/${item.phase ?? 'lifecycle'}`}
          rowTestId={(item) => `gitops-operation-${item.resourceId}-${item.phase ?? 'lifecycle'}`}
          columns={[
            {
              id: 'resource',
              label: 'Resource',
              render: (item) => (
                <>
                  {item.kind} / {item.name}
                  <p>
                    <code>{item.resourceId}</code>
                  </p>
                </>
              ),
            },
            { id: 'phase', label: 'Phase', render: (item) => phaseLabel(item.phase) },
            {
              id: 'action',
              label: 'Action',
              render: (item) => item.lifecycle?.action ?? item.plan?.action ?? 'Not evaluated',
            },
            { id: 'status', label: 'Status', render: (item) => item.status },
            {
              id: 'fields',
              label: 'Changed fields',
              render: (item) =>
                (item.lifecycle?.changedFields ?? item.plan?.changedFields ?? []).join(', ') ||
                'None recorded',
            },
            {
              id: 'revision',
              label: 'Plan / evaluation',
              render: (item) =>
                item.plan
                  ? `${item.plan.revision} · ${item.plan.evaluatedAtUtc}`
                  : (item.lifecycle?.lastCompletedStep ?? 'Pending'),
            },
            {
              id: 'blocker',
              label: 'Blocker',
              render: (item) => (item.blocker ? safeCode(item.blocker) : 'None'),
            },
          ]}
        />
      )}
      <PageControls label="operations" page={page} value={operations.resource} onPage={setPage} />
    </article>
  )
}
