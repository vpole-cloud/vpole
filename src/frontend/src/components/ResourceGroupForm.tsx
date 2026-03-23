import { useState } from 'react'
import type { ResourceGroup, ResourceGroupInput } from '../types'

interface ResourceGroupFormProps {
  mode: 'create' | 'edit'
  initialValue?: ResourceGroup | null
  submitStatus: 'idle' | 'loading' | 'success' | 'error'
  submitError?: string
  onSubmit: (input: ResourceGroupInput) => Promise<void>
  onCancelEdit?: () => void
}

export function ResourceGroupForm({
  mode,
  initialValue,
  submitStatus,
  submitError,
  onSubmit,
  onCancelEdit,
}: ResourceGroupFormProps) {
  const [name, setName] = useState(initialValue?.name ?? '')
  const [description, setDescription] = useState(initialValue?.description ?? '')
  const [tagsText, setTagsText] = useState(initialValue?.tags.join(', ') ?? '')
  const [validationError, setValidationError] = useState('')

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()

    if (!name.trim()) {
      setValidationError('Name is required.')
      return
    }

    setValidationError('')

    await onSubmit({
      name: name.trim(),
      description: description.trim(),
      tags: tagsText
        .split(',')
        .map((tag) => tag.trim())
        .filter(Boolean),
    })

    if (mode === 'create') {
      setName('')
      setDescription('')
      setTagsText('')
    }
  }

  return (
    <section className="panel resource-group-form-panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">
            {mode === 'create' ? 'Create' : 'Edit'}
          </p>
          <h2 className="panel__title">
            {mode === 'create' ? 'New resource group' : 'Edit resource group'}
          </h2>
        </div>
      </div>

      <form
        className="resource-group-form"
        onSubmit={(event) => void handleSubmit(event)}
      >
        <label className="field">
          <span className="field__label">Name</span>
          <input
            className="field__control"
            value={name}
            onChange={(event) => setName(event.target.value)}
            disabled={mode === 'edit'}
            maxLength={50}
            placeholder="prod-apps"
          />
          {mode === 'edit' && (
            <span className="field__hint">
              Resource group names are fixed after creation.
            </span>
          )}
        </label>

        <label className="field">
          <span className="field__label">Description</span>
          <textarea
            className="field__control field__control--textarea"
            value={description}
            onChange={(event) => setDescription(event.target.value)}
            maxLength={200}
            placeholder="Boundary for production workloads and shared services."
          />
        </label>

        <label className="field">
          <span className="field__label">Tags</span>
          <input
            className="field__control"
            value={tagsText}
            onChange={(event) => setTagsText(event.target.value)}
            placeholder="production, apps, public"
          />
          <span className="field__hint">Separate tags with commas.</span>
        </label>

        {(validationError || submitError) && (
          <div className="form-message form-message--error">
            {validationError || submitError}
          </div>
        )}

        {mode === 'edit' && (
          <div className="form-message">
            Only description and tags can be changed after creation.
          </div>
        )}

        <div className="resource-group-form__actions">
          <button
            className="button button--primary"
            disabled={submitStatus === 'loading'}
            type="submit"
          >
            {submitStatus === 'loading'
              ? 'Saving...'
              : mode === 'create'
                ? 'Create group'
                : 'Save changes'}
          </button>
          {mode === 'edit' && onCancelEdit && (
            <button className="button" onClick={onCancelEdit} type="button">
              Cancel
            </button>
          )}
        </div>
      </form>
    </section>
  )
}
