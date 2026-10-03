import { useEffect, useId, useState } from 'react'
import { validationMessage } from '../api'
import { Button, EmptyState, Field } from '../ui/components'
import type { ResourceGroup, ResourceGroupInput, LoadStatus } from '../types'

interface ResourceGroupFormProps {
  mode: 'create' | 'edit'
  initialValue?: ResourceGroup | null
  submitStatus: LoadStatus
  submitError?: string
  onSubmit: (input: ResourceGroupInput) => Promise<void>
  onCancelEdit?: () => void
  onDirtyChange?: (dirty: boolean) => void
}
const signature = (value: ResourceGroupInput) => JSON.stringify({ name: value.name.trim(), description: value.description.trim(), tags: [...new Set(value.tags)].sort() })
const empty = { name: '', description: '', tags: [] }
export function ResourceGroupForm({ mode, initialValue, submitStatus, submitError, onSubmit, onCancelEdit, onDirtyChange }: ResourceGroupFormProps) {
  const initial = initialValue ?? empty
  const [name, setName] = useState(initial.name)
  const [description, setDescription] = useState(initial.description)
  const [tagsText, setTagsText] = useState(initial.tags.join(', '))
  const input = { name: name.trim(), description: description.trim(), tags: tagsText.split(',').map((tag) => tag.trim()).filter(Boolean) }
  const [baseline, setBaseline] = useState(signature(initial))
  const dirty = signature(input) !== baseline
  const [loadedSignature, setLoadedSignature] = useState(signature(initial))
  if (loadedSignature !== signature(initial)) {
    setLoadedSignature(signature(initial))
    if (!dirty) { setName(initial.name); setDescription(initial.description); setTagsText(initial.tags.join(', ')); setBaseline(signature(initial)) }
  }
  const [failure, setFailure] = useState<unknown>(null)
  const [validationError, setValidationError] = useState('')
  const id = useId()
  useEffect(() => { onDirtyChange?.(dirty); return () => onDirtyChange?.(false) }, [dirty, onDirtyChange])
  const fieldError = (field: string) => validationMessage(failure, field)
  return <section className="panel resource-group-form-panel">
    <div className="panel__header"><h2 className="panel__title">{mode === 'create' ? 'New resource group' : 'Edit resource group'}</h2></div>
    <form className="resource-group-form" onSubmit={async (event) => {
      event.preventDefault()
      if (!input.name) { setValidationError('Name is required.'); return }
      setValidationError(''); setFailure(null)
      try {
        await onSubmit(input)
        setBaseline(signature(input))
      } catch (error) { setFailure(error) }
    }}>
      <Field label="Name" hint={mode === 'edit' ? 'Resource group names are fixed after creation.' : undefined}>
        <input className="field__control" value={name} onChange={(event) => { setName(event.target.value); setFailure(null); setValidationError('') }}
          disabled={mode === 'edit' || submitStatus === 'loading'} maxLength={50} placeholder="prod-apps"
          aria-invalid={Boolean(fieldError('name') || validationError)} aria-describedby={`${id}-name-error`} />
        <span id={`${id}-name-error`} className="field-error">{fieldError('name') || validationError}</span>
      </Field>
      <Field label="Description"><textarea className="field__control field__control--textarea" value={description}
        onChange={(event) => { setDescription(event.target.value); setFailure(null) }} disabled={submitStatus === 'loading'} maxLength={200}
        placeholder="Boundary for production workloads and shared services." aria-invalid={Boolean(fieldError('description'))} aria-describedby={`${id}-description-error`} />
        <span id={`${id}-description-error`} className="field-error">{fieldError('description')}</span>
      </Field>
      <Field label="Tags" hint="Separate tags with commas."><input className="field__control" value={tagsText}
        onChange={(event) => { setTagsText(event.target.value); setFailure(null) }} disabled={submitStatus === 'loading'} placeholder="production, apps, public"
        aria-invalid={Boolean(fieldError('tags'))} aria-describedby={`${id}-tags-error`} />
        <span id={`${id}-tags-error`} className="field-error">{fieldError('tags')}</span>
      </Field>
      {(failure || submitError) && <EmptyState title="Unable to save this group." error>{failure instanceof Error ? failure.message : submitError}</EmptyState>}
      <div className="resource-group-form__actions"><Button variant="primary" disabled={submitStatus === 'loading'} type="submit">
        {submitStatus === 'loading' ? 'Saving…' : mode === 'create' ? 'Create group' : 'Save changes'}</Button>
        {onCancelEdit && <Button disabled={submitStatus === 'loading'} onClick={onCancelEdit}>Discard changes</Button>}
      </div>
    </form>
  </section>
}
