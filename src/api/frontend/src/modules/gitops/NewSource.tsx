import { useEffect, useState } from 'react'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { Button, Field, Notice } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { createSource, safeGitOpsError } from './api'
import { useGitOpsMutation } from './useGitOpsQuery'
import { RepositoryPicker } from './RepositoryPicker'
import { sourceLabel, emptyLocation, type GitOpsSection } from './model'
import type { PrunePolicy, SourceInput } from './types'
export function NewSource({
  active,
  actions,
}: {
  active: boolean
  actions: WorkspaceActions<GitOpsSection>
}) {
  const { tenant } = useAppServices()
  const [location, setLocation] = useState(emptyLocation)
  const [interval, setInterval] = useState(300)
  const [prune, setPrune] = useState<PrunePolicy>('Retain')
  const [submitted, setSubmitted] = useState<SourceInput | null>(null)
  const [error, setError] = useState('')
  const dirty = Boolean(
    location.connectionId ||
    location.manifestDirectory ||
    interval !== 300 ||
    prune !== 'Retain' ||
    submitted,
  )
  const mutation = useGitOpsMutation('create', active && tenant.canManage)
  const formScope = `${mutation.origin.identity}:${tenant.canManage}`
  const [previousScope, setPreviousScope] = useState(formScope)
  if (previousScope !== formScope) {
    setPreviousScope(formScope)
    setLocation(emptyLocation)
    setInterval(300)
    setPrune('Retain')
    setSubmitted(null)
    setError('')
  }
  useUnsavedChanges(dirty, 'New GitOps source')
  const markDirty = actions.dirty
  useEffect(() => {
    markDirty('create', dirty)
    return () => markDirty('create', false)
  }, [markDirty, dirty])
  async function submit() {
    const command = submitted ?? {
      ...location,
      pollIntervalSeconds: interval,
      prune,
      idempotencyKey: crypto.randomUUID(),
    }
    setSubmitted(command)
    setError('')
    try {
      await mutation.run(
        (api, signal) => createSource(api, command, signal),
        async (source, guard) => {
          guard()
          setSubmitted(null)
          actions.dirty('create', false)
          actions.created(source.id, sourceLabel(source))
        },
      )
    } catch (failure) {
      const safe = safeGitOpsError(failure)
      setError(safe.message)
      if (safe.status > 0 && safe.status < 500 && submitted === null) setSubmitted(null)
    }
  }
  return (
    <section className="panel">
      <h1>New GitOps source</h1>
      <p>Read manifests from a GitHub repository and synchronize the managed configuration.</p>
      {error && <Notice tone="error">{error}</Notice>}
      {submitted && !mutation.busy && (
        <Notice>
          The creation response is uncertain. Repeat the same request with its original key, or
          inspect existing sources before starting another draft.
        </Notice>
      )}
      <form
        onSubmit={(event) => {
          event.preventDefault()
          void submit()
        }}
      >
        <RepositoryPicker
          value={location}
          onChange={setLocation}
          active={active}
          disabled={mutation.busy || submitted !== null}
        />
        <Field label="Polling interval (seconds)">
          <input
            className="field__control"
            type="number"
            min={60}
            max={86400}
            required
            disabled={mutation.busy || submitted !== null}
            value={interval}
            onChange={(event) => setInterval(Number(event.target.value))}
          />
        </Field>
        <Field label="Prune policy">
          <select
            className="field__control"
            disabled={mutation.busy || submitted !== null}
            value={prune}
            onChange={(event) => setPrune(event.target.value as PrunePolicy)}
          >
            <option value="Retain">Retain removed declarations</option>
            <option value="Prune">Prune eligible resources</option>
          </select>
        </Field>
        <p>Protected data and empty desired state still require a reviewed confirmation.</p>
        <Button
          variant="primary"
          type="submit"
          disabled={
            !tenant.canManage ||
            mutation.busy ||
            !location.connectionId ||
            !location.externalRepositoryId ||
            !location.branch
          }
        >
          {mutation.busy
            ? 'Creating…'
            : submitted
              ? 'Repeat same creation request'
              : 'Create source'}
        </Button>
        {submitted && !mutation.busy && (
          <Button
            onClick={() => {
              setSubmitted(null)
              setError('')
            }}
          >
            Start another draft after inspection
          </Button>
        )}
      </form>
    </section>
  )
}
