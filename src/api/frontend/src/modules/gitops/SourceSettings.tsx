// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useEffect, useState } from 'react'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { Button, Field, Notice } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import type { GitOpsSource, PrunePolicy } from './types'
import { sourceLabel, type GitOpsSection } from './model'
import { sourceDirty, sourceDraft, reapplySourceDraft } from './configurationModel'
import { safeGitOpsError, updateSource } from './api'
import { useGitOpsMutation } from './useGitOpsQuery'
export function SourceSettings({
  source,
  active,
  healthy,
  actions,
}: {
  source: GitOpsSource
  active: boolean
  healthy: boolean
  actions: WorkspaceActions<GitOpsSection>
}) {
  const { tenant } = useAppServices()
  const mutation = useGitOpsMutation(source.id + ':settings', tenant.canManage && active)
  const [state, setState] = useState(() => sourceDraft(source))
  const [accepted, setAccepted] = useState(false)
  const [error, setError] = useState('')
  const [uncertain, setUncertain] = useState<Parameters<typeof updateSource>[1] | null>(null)
  const formScope = `${mutation.origin.identity}:${source.id}:${tenant.canManage}`
  const [previousScope, setPreviousScope] = useState(formScope)
  if (previousScope !== formScope) {
    setPreviousScope(formScope)
    setState(sourceDraft(source))
    setUncertain(null)
    setError('')
    setAccepted(false)
  }
  const dirty = sourceDirty(state)
  if (source.revision > state.baseline.revision && !dirty && !uncertain)
    setState(sourceDraft(source))
  const conflict = source.revision > state.baseline.revision
  const waitingRead = state.baseline.revision > source.revision
  const editable = source.activity === 'Active' || source.activity === 'Paused'

  useUnsavedChanges(dirty || uncertain !== null, 'GitOps source settings')
  const markDirty = actions.dirty
  useEffect(() => {
    markDirty(source.id, dirty || uncertain !== null)
    return () => markDirty(source.id, false)
  }, [markDirty, source.id, dirty, uncertain])
  async function save() {
    if (!healthy || !editable || conflict || waitingRead) return
    const command = uncertain ?? {
      sourceId: source.id,
      expectedRevision: state.baseline.revision,
      ...state.draft,
    }
    setUncertain(command)
    setError('')
    setAccepted(false)
    try {
      await mutation.run(
        (api, signal) => updateSource(api, command, signal),
        async (value, guard) => {
          guard()
          setState(sourceDraft(value))
          setUncertain(null)
          setAccepted(true)
        },
      )
    } catch (failure) {
      const safe = safeGitOpsError(failure)
      setError(safe.message)
      if (safe.status > 0 && safe.status < 500 && uncertain === null) setUncertain(null)
    }
  }
  return (
    <section className="panel">
      <h2>Source settings</h2>
      <p>Repository location changes use a relocation preview.</p>
      {error && <Notice tone="error">{error}</Notice>}
      {accepted && (
        <Notice>Settings accepted. Current server metadata is refreshed separately.</Notice>
      )}
      {conflict && (
        <Notice>
          Source configuration changed. Review and reapply your edited fields or discard the draft.
          <div className="row-actions">
            <Button
              onClick={() => {
                setState(reapplySourceDraft(state, source))
                setUncertain(null)
                setError('')
              }}
            >
              Review and reapply
            </Button>
            <Button
              onClick={() => {
                setState(sourceDraft(source))
                setUncertain(null)
                setError('')
              }}
            >
              Discard draft
            </Button>
          </div>
        </Notice>
      )}
      {waitingRead && <Notice>Saved revision is awaiting a fresh server read.</Notice>}
      {uncertain && !mutation.busy && (
        <Notice>
          The write response is uncertain. Refresh current settings before repeating the same
          captured command.
        </Notice>
      )}
      <form
        onSubmit={(event) => {
          event.preventDefault()
          void save()
        }}
      >
        <fieldset disabled={!tenant.canManage || !editable || mutation.busy || uncertain !== null}>
          <legend>{sourceLabel(source)}</legend>
          <Field label="Polling interval (seconds)">
            <input
              className="field__control"
              type="number"
              min={60}
              max={86400}
              required
              value={state.draft.pollIntervalSeconds}
              onChange={(event) =>
                setState({
                  ...state,
                  draft: { ...state.draft, pollIntervalSeconds: Number(event.target.value) },
                })
              }
            />
          </Field>
          <Field label="Prune policy">
            <select
              className="field__control"
              value={state.draft.prune}
              onChange={(event) =>
                setState({
                  ...state,
                  draft: { ...state.draft, prune: event.target.value as PrunePolicy },
                })
              }
            >
              <option value="Retain">Retain removed declarations</option>
              <option value="Prune">Prune eligible resources</option>
            </select>
          </Field>
        </fieldset>
        <p>Prune policy never confirms protected deletion or an empty desired set.</p>
        <Button
          type="submit"
          variant="primary"
          disabled={
            !tenant.canManage ||
            !editable ||
            !healthy ||
            conflict ||
            waitingRead ||
            mutation.busy ||
            (!dirty && !uncertain)
          }
        >
          {mutation.busy ? 'Saving…' : uncertain ? 'Repeat same settings command' : 'Save settings'}
        </Button>
      </form>
    </section>
  )
}
