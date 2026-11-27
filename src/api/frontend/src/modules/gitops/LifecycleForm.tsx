// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useState } from 'react'
import { ApiError } from '../../api/transport'
import { Button, Field, Notice } from '../../ui/components'
import { AdoptionPicker } from './AdoptionPicker'
import { InventorySelection } from './InventorySelection'
import {
  initialLifecycleDraft,
  previewRequest,
  resourceIdentity,
  validLifecycleDraft,
  type LifecycleDraft,
  type LifecycleIntent,
} from './lifecycleModel'
import { previewActivityAllowed } from './previewModel'
import { RepositoryPicker } from './RepositoryPicker'
import type { GitOpsSource, PreviewRequest, ResourceIdentity } from './types'
const intents: { value: LifecycleIntent; label: string }[] = [
  { value: 'ResourceChanges', label: 'Repository changes' },
  { value: 'Adoption', label: 'Adopt manual resources' },
  { value: 'Removal', label: 'Remove selected resources' },
  { value: 'Replacement', label: 'Replace or explicitly rename resources' },
  { value: 'Relocation', label: 'Change repository location' },
  { value: 'Detach', label: 'Detach and return manual control' },
]
export function LifecycleForm({
  source,
  active,
  allowed,
  busy,
  error,
  unknown,
  draft,
  onChange,
  onSubmit,
  onInspect,
}: {
  source: GitOpsSource
  active: boolean
  allowed: boolean
  busy: boolean
  error: ApiError | null
  unknown: boolean
  draft: LifecycleDraft
  onChange: (draft: LifecycleDraft) => void
  onSubmit: (request: PreviewRequest) => void
  onInspect: () => void
}) {
  const [baseline, setBaseline] = useState({
    revision: source.revision,
    inventoryRevision: source.inventory.revision,
  })
  const current =
    baseline.revision === source.revision &&
    baseline.inventoryRevision === source.inventory.revision
  const disabled = !allowed || busy || unknown
  const ready =
    !disabled &&
    current &&
    validLifecycleDraft(draft, source) &&
    previewActivityAllowed(source, draft.intent)
  function chooseResources(resources: ResourceIdentity[]) {
    if (draft.intent === 'Removal')
      onChange({ ...draft, resources: resources.map(resourceIdentity) })
    if (draft.intent === 'Replacement')
      onChange({
        ...draft,
        replacements: resources.map((original) => ({
          original: resourceIdentity(original),
          desired: draft.replacements.find(
            (item) => item.original.resourceId === original.resourceId,
          )?.desired ?? { kind: original.kind, name: original.name },
        })),
      })
  }
  return (
    <form
      aria-label="Build lifecycle preview"
      onSubmit={(event) => {
        event.preventDefault()
        if (ready) onSubmit(previewRequest(draft, source.revision))
      }}
    >
      <h3>Build a lifecycle preview</h3>
      <p>
        The server builds the concrete action graph and checks resource compatibility. Creating a
        preview does not apply its actions.
      </p>
      <Field label="Lifecycle intent">
        <select
          className="field__control"
          value={draft.intent}
          disabled={disabled}
          onChange={(event) =>
            onChange(initialLifecycleDraft(event.target.value as LifecycleIntent, source))
          }
        >
          {intents.map((item) => (
            <option
              key={item.value}
              value={item.value}
              disabled={!previewActivityAllowed(source, item.value)}
            >
              {item.label}
            </option>
          ))}
        </select>
      </Field>
      {!current && (
        <Notice>
          Source or inventory changed while this draft was open. Review the selected IDs and
          settings before building a new plan.{' '}
          <Button
            disabled={disabled}
            onClick={() =>
              setBaseline({
                revision: source.revision,
                inventoryRevision: source.inventory.revision,
              })
            }
          >
            Use reviewed source revision
          </Button>
        </Notice>
      )}
      {draft.intent === 'ResourceChanges' && (
        <>
          <p>
            For declarations absent from Git, retain keeps resources and ownership pending removal;
            remove requests a reviewed deletion plan. Explicit rename is selected separately.
          </p>
          <Field label="Declarations absent from the snapshot">
            <select
              className="field__control"
              value={draft.removal}
              disabled={disabled}
              onChange={(event) =>
                onChange({ ...draft, removal: event.target.value as 'Retain' | 'Remove' })
              }
            >
              <option value="Retain" disabled={source.prune !== 'Retain'}>
                Retain resources and ownership
              </option>
              <option value="Remove">Review removal</option>
            </select>
          </Field>
        </>
      )}
      {draft.intent === 'Adoption' && (
        <>
          <p>
            Adoption keeps existing IDs and unmanaged data. Parent adoption does not claim its
            children.
          </p>
          <AdoptionPicker
            sourceId={source.id}
            active={active}
            disabled={disabled}
            selected={draft.resources}
            onChange={(resources) => onChange({ ...draft, resources })}
          />
          <SelectedResources
            resources={draft.resources}
            disabled={disabled}
            onRemove={(id) =>
              onChange({
                ...draft,
                resources: draft.resources.filter((item) => item.resourceId !== id),
              })
            }
          />
        </>
      )}
      {(draft.intent === 'Removal' || draft.intent === 'Replacement') && (
        <>
          <InventorySelection
            source={source}
            active={active}
            disabled={disabled}
            selected={
              draft.intent === 'Removal'
                ? draft.resources
                : draft.replacements.map((item) => item.original)
            }
            onChange={chooseResources}
          />
          {draft.intent === 'Removal' ? (
            <SelectedResources
              resources={draft.resources}
              disabled={disabled}
              onRemove={(id) =>
                onChange({
                  ...draft,
                  resources: draft.resources.filter((item) => item.resourceId !== id),
                })
              }
            />
          ) : (
            <>
              <Notice>
                Replacement uses delete then create. Commit the desired address in Git first. The
                server reserves new IDs and includes required same-source dependents; it performs no
                data migration.
              </Notice>
              {draft.replacements.map((item, index) => (
                <div key={item.original.resourceId}>
                  <p>
                    {item.original.kind}/{item.original.name} · {item.original.resourceId}
                  </p>
                  <Field label={`Desired name for ${item.original.kind}/${item.original.name}`}>
                    <input
                      className="field__control"
                      required
                      value={item.desired.name}
                      disabled={disabled}
                      onChange={(event) =>
                        onChange({
                          ...draft,
                          replacements: draft.replacements.map((replacement, at) =>
                            at === index
                              ? {
                                  ...replacement,
                                  desired: {
                                    kind: replacement.original.kind,
                                    name: event.target.value,
                                  },
                                }
                              : replacement,
                          ),
                        })
                      }
                    />
                  </Field>
                  <Button
                    disabled={disabled}
                    onClick={() =>
                      onChange({
                        ...draft,
                        replacements: draft.replacements.filter((_, at) => at !== index),
                      })
                    }
                  >
                    Remove replacement selection
                  </Button>
                </div>
              ))}
            </>
          )}
        </>
      )}
      {draft.intent === 'Relocation' && (
        <>
          <Notice>
            Relocation changes the repository input, not physical workload placement. Review the
            consequences for the complete inventory at the destination commit.
          </Notice>
          <RepositoryPicker
            value={draft.destination}
            onChange={(destination) => onChange({ ...draft, destination })}
            active={active}
            disabled={disabled}
          />
        </>
      )}
      {draft.intent === 'Detach' && (
        <Notice>
          Detach retains resources, credentials and runtime configuration, releases ownership
          durably and ends in Detached. It works without repository access; future Git control
          requires a new source and explicit adoption.
        </Notice>
      )}
      {error && <Notice tone="error">{error.message}</Notice>}
      {unknown && (
        <Notice>
          The preview response was not received. Inspect preview history before starting another
          request. <Button onClick={onInspect}>Inspect preview history</Button>
        </Notice>
      )}
      <Button type="submit" variant="primary" disabled={!ready}>
        Build lifecycle preview
      </Button>
    </form>
  )
}
function SelectedResources({
  resources,
  disabled,
  onRemove,
}: {
  resources: ResourceIdentity[]
  disabled: boolean
  onRemove: (id: string) => void
}) {
  return (
    <section aria-label="Selected resource identities">
      <h4>Selected identities ({resources.length})</h4>
      <ul>
        {resources.map((item) => (
          <li key={`${item.kind}/${item.resourceId}`}>
            {item.kind}/{item.name} · {item.resourceId}{' '}
            <Button disabled={disabled} onClick={() => onRemove(item.resourceId)}>
              Remove selection
            </Button>
          </li>
        ))}
      </ul>
    </section>
  )
}
