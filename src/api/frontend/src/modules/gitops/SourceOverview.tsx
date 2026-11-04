import { Button, Notice, StatusBadge } from '../../ui/components'
import { SourceCommands } from './SourceCommands'
import { safeCode } from './model'
import type { GitOpsSource } from './types'
export function SourceOverview({
  source,
  active,
  healthy,
  onRun,
  onPreview,
  onDeleted,
  refreshSource,
}: {
  source: GitOpsSource
  active: boolean
  healthy: boolean
  onRun: (id: string) => void
  onPreview: (id?: string) => void
  onDeleted: () => void
  refreshSource: () => void
}) {
  const sync = source.synchronization
  return (
    <section className="panel">
      <h2>Source overview</h2>
      <p>Repository: {source.repository.displayUrl || source.repository.externalRepositoryId}</p>
      <p>
        Branch: {source.repository.branch} · directory:{' '}
        {source.repository.manifestDirectory || 'Repository root'} · revision {source.revision}
      </p>
      <p>
        Activity: <StatusBadge>{source.activity}</StatusBadge> · Execution:{' '}
        <StatusBadge>{source.status}</StatusBadge>
      </p>
      <dl>
        <dt>Requested / completed generations</dt>
        <dd>
          {sync.requestedGeneration} / {sync.completedGeneration}
        </dd>
        <dt>Observed commit</dt>
        <dd>
          <code>{sync.observedCommit ?? 'Not resolved'}</code>
        </dd>
        <dt>Attempted commit</dt>
        <dd>
          <code>{sync.attemptedCommit ?? 'No accepted input'}</code>
        </dd>
        <dt>Fully applied commit</dt>
        <dd>
          <code>{sync.fullyAppliedCommit ?? 'Not applied'}</code>
        </dd>
        <dt>Inventory</dt>
        <dd>
          {source.inventory.owned} owned · {source.inventory.pendingRemoval} pending removal ·
          revision {source.inventory.revision}
        </dd>
        <dt>Prune policy</dt>
        <dd>{source.prune}</dd>
      </dl>
      <p>
        Configuration applied describes managed Pole configuration. Runtime readiness is reported by
        each resource.
      </p>
      {source.activity === 'Paused' && (
        <Notice>
          Ownership remains protected while paused. Explicit confirmed lifecycle and previously
          requested runtime cleanup can continue.
        </Notice>
      )}
      {source.activity === 'Detached' && (
        <Notice>
          Source is detached. Resources retain their configuration and manual control. New Git
          ownership requires a new source and adoption.
        </Notice>
      )}
      <SourceCommands
        source={source}
        active={active}
        healthy={healthy}
        onDeleted={onDeleted}
        onAccepted={refreshSource}
      />
      <div className="row-actions">
        <Button disabled={!sync.currentRunId} onClick={() => onRun(sync.currentRunId!)}>
          Open current run
        </Button>
        <Button disabled={!sync.latestRunId} onClick={() => onRun(sync.latestRunId!)}>
          Open latest run
        </Button>
        <Button onClick={() => onPreview()}>Lifecycle previews</Button>
      </div>
      {source.latestRun?.lifecycle && (
        <Button onClick={() => onPreview(source.latestRun!.lifecycle!.previewId)}>
          Open run preview
        </Button>
      )}
      {source.latestRun?.diagnostics.map((item, index) => (
        <Notice key={index} tone="error">
          {safeCode(item.code)}
          {item.file ? ` · ${item.file}` : ''}
          {item.field ? ` · ${item.field}` : ''}
        </Notice>
      ))}
    </section>
  )
}
