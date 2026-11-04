import { useState } from 'react'
import { useAppServices } from '../../shell/AppContext'
import { Button, ConfirmationDialog, Notice } from '../../ui/components'
import { commandSource, safeGitOpsError } from './api'
import { sourceExecuting, retryAvailable } from './model'
import { useGitOpsMutation } from './useGitOpsQuery'
import type { GitOpsSource, GitOpsRun, SourceCommand } from './types'
export function SourceCommands({
  source,
  active,
  healthy,
  run,
  onDeleted,
  onAccepted,
  onlyRetry = false,
}: {
  source: GitOpsSource
  active: boolean
  healthy: boolean
  run?: GitOpsRun | null
  onDeleted: () => void
  onAccepted: () => void
  onlyRetry?: boolean
}) {
  const { tenant } = useAppServices()
  const [command, setCommand] = useState<SourceCommand | null>(null)
  const [error, setError] = useState('')
  const [unknown, setUnknown] = useState(false)
  const [notice, setNotice] = useState('')
  const mutation = useGitOpsMutation(source.id + ':commands', tenant.canManage && active)
  const lifetime = `${mutation.origin.identity}:${source.id}:${tenant.canManage}:${active}`
  const [previousLifetime, setPreviousLifetime] = useState(lifetime)
  if (previousLifetime !== lifetime) {
    setPreviousLifetime(lifetime)
    setCommand(null)
    setUnknown(false)
    setError('')
  }
  const current = command?.sourceId === source.id && command.expectedRevision === source.revision
  const canDelete =
    source.inventory.owned === 0 && !sourceExecuting(source) && source.activity !== 'Detaching'
  const latest = run ?? source.latestRun
  const begin = (action: SourceCommand['action']) => {
    setError('')
    setUnknown(false)
    const base = { sourceId: source.id, expectedRevision: source.revision }
    setCommand(
      action === 'sync'
        ? { ...base, action, idempotencyKey: crypto.randomUUID() }
        : action === 'retry'
          ? { ...base, action, runId: latest!.id, idempotencyKey: crypto.randomUUID() }
          : { ...base, action },
    )
  }
  const permitted =
    command &&
    current &&
    healthy &&
    (command.action === 'delete'
      ? canDelete
      : command.action === 'resume'
        ? source.activity === 'Paused'
        : command.action === 'pause'
          ? source.activity === 'Active'
          : command.action === 'retry'
            ? retryAvailable(source, latest)
            : source.activity === 'Active')
  async function submit() {
    if (!command || !permitted) return
    const captured = command
    setError('')
    try {
      await mutation.run(
        (api, signal) => commandSource(api, captured, signal),
        async (_value, guard) => {
          guard()
          setCommand(null)
          setUnknown(false)
          setNotice(
            captured.action + ' accepted. Server execution and cleanup are reported separately.',
          )
          if (captured.action === 'delete') onDeleted()
          else onAccepted()
        },
      )
    } catch (failure) {
      const safe = safeGitOpsError(failure)
      setError(safe.message)
      setUnknown(safe.status === 0 || safe.status >= 500)
    }
  }
  return (
    <>
      <div className="row-actions">
        <Button
          hidden={onlyRetry}
          disabled={!tenant.canManage || !healthy || source.activity !== 'Active' || mutation.busy}
          onClick={() => begin('sync')}
        >
          Sync
        </Button>
        <Button
          disabled={
            !tenant.canManage || !healthy || !retryAvailable(source, latest) || mutation.busy
          }
          onClick={() => begin('retry')}
        >
          Retry run
        </Button>
        {!onlyRetry && source.activity === 'Active' && (
          <Button
            disabled={!tenant.canManage || !healthy || mutation.busy}
            onClick={() => begin('pause')}
          >
            Pause
          </Button>
        )}
        {!onlyRetry && source.activity === 'Paused' && (
          <Button
            disabled={!tenant.canManage || !healthy || mutation.busy}
            onClick={() => begin('resume')}
          >
            Resume
          </Button>
        )}
        <Button
          variant="danger"
          hidden={onlyRetry}
          disabled={!tenant.canManage || !healthy || !canDelete || mutation.busy}
          onClick={() => begin('delete')}
        >
          Delete source
        </Button>
      </div>
      {notice && <Notice>{notice}</Notice>}
      {command && (
        <ConfirmationDialog
          title={
            command.action === 'sync'
              ? 'Sync current branch?'
              : command.action === 'retry'
                ? 'Retry the fixed run input?'
                : command.action === 'pause'
                  ? 'Pause GitOps mutations?'
                  : command.action === 'resume'
                    ? 'Resume with a fresh Sync?'
                    : 'Delete this empty source?'
          }
          confirmLabel={unknown ? 'Repeat same command' : 'Confirm ' + command.action}
          busy={mutation.busy}
          confirmDisabled={!permitted || !tenant.canManage}
          onCancel={() => {
            setCommand(null)
            setUnknown(false)
          }}
          onConfirm={() => void submit()}
        >
          <p>
            Source {source.id}, configuration revision {command.expectedRevision}.
          </p>
          {command.action === 'pause' && (
            <p>Ownership remains protected. Already requested runtime cleanup continues.</p>
          )}
          {command.action === 'resume' && (
            <p>Resume reads the current branch. Interrupted dangerous plans need a new preview.</p>
          )}
          {command.action === 'retry' && (
            <p>Run {command.runId} retains its accepted commit and input.</p>
          )}
          {command.action === 'delete' && (
            <p>
              This removes the empty source from normal reads and preserves its audit/idempotency
              tombstone.
            </p>
          )}
          {!current && (
            <Notice>
              Source revision changed. Close this confirmation and review current metadata.
            </Notice>
          )}
          {unknown && (
            <Notice>
              <Button onClick={onAccepted}>Refresh source state</Button>
              The server outcome is unknown. Refresh before repeating this exact captured command.
            </Notice>
          )}
          {error && <Notice tone="error">{error}</Notice>}
        </ConfirmationDialog>
      )}
    </>
  )
}
