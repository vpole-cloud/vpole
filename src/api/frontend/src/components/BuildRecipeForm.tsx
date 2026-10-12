import { fieldsFor } from '../modules/web-services/recipeDraft'
import { useEffect, useState } from 'react'
import { validationMessage } from '../api'
import type { SourceBuildConfiguration, SourceBuildRecipe } from '../types'
import { Button, ConfirmationDialog, Notice } from '../ui/components'


export function BuildRecipeForm({ configuration, canManage, busy, onSave, onDirtyChange }: {
  configuration: SourceBuildConfiguration; canManage: boolean; busy: boolean;
  onSave: (recipe: SourceBuildRecipe) => Promise<SourceBuildConfiguration>; onDirtyChange: (dirty: boolean) => void
}) {
  const [fields, setFields] = useState(() => fieldsFor(configuration.recipe))
  const [saved, setSaved] = useState(() => JSON.stringify(fields))
  const [serverRecipe, setServerRecipe] = useState(() => JSON.stringify(configuration.recipe))
  const [failure, setFailure] = useState<unknown>(null)
  const [discarding, setDiscarding] = useState(false)
  const [reviewing, setReviewing] = useState(false)
  const dirty = JSON.stringify(fields) !== saved
  const currentRecipe = JSON.stringify(configuration.recipe)
  const conflict = dirty && currentRecipe !== serverRecipe
  if (!dirty && currentRecipe !== serverRecipe) {
    const fields = fieldsFor(configuration.recipe)
    setFields(fields); setSaved(JSON.stringify(fields)); setServerRecipe(currentRecipe)
  }
  useEffect(() => { onDirtyChange(dirty); return () => onDirtyChange(false) }, [dirty, onDirtyChange])
  const labels = { sourceRoot: 'Source directory', manifestPath: 'Manifest path', commands: 'Build commands', startCommand: 'Start command', dockerfile: 'Dockerfile' }
  return <details data-testid={`build-recipe-${configuration.id}`} className="build-recipe"><summary>Build recipe · {configuration.recipe.isCustomized ? 'Customized' : 'Detected'}</summary>
    {conflict && <Notice tone="error">The saved recipe changed while this draft was open. Your draft is kept.<Button onClick={() => setReviewing(true)}>Review saved recipe</Button></Notice>}
    <p className="field__hint">Changes apply to future builds. Running jobs retain their recorded recipe and commit.</p>
    <p className="field__hint">Edit the Dockerfile to change build and start commands. The source directory and detected commands are read-only reference information.</p>
    <form onSubmit={(event) => {
      event.preventDefault()
      if (!canManage || busy || !dirty || conflict) return
      setFailure(null)
      const recipe = { ...configuration.recipe, ...fields, startCommand: fields.startCommand.trim() || null,
        commands: fields.commands.split('\n').map((command) => command.trim()).filter(Boolean) }
      void onSave(recipe).then((updated) => {
        const fields = fieldsFor(updated.recipe); setFields(fields); setSaved(JSON.stringify(fields)); setServerRecipe(JSON.stringify(updated.recipe))
      }).catch(setFailure)
    }}><fieldset className="web-service-form__fields" disabled={busy}>
      {(['sourceRoot', 'manifestPath', 'commands', 'startCommand', 'dockerfile'] as const).map((field) => <label className="field" key={field}>
        <span className="field__label">{labels[field]}</span>
        {field === 'commands' || field === 'dockerfile' ? <textarea className={`field__control field__control--textarea${field === 'dockerfile' ? ' build-recipe__dockerfile' : ''}`}
          value={fields[field]} readOnly={!canManage || field === 'commands'} onChange={(event) => setFields((fields) => ({ ...fields, [field]: event.target.value }))} /> :
          <input className="field__control" value={fields[field]} readOnly={!canManage || field === 'sourceRoot' || field === 'startCommand'} onChange={(event) => setFields((fields) => ({ ...fields, [field]: event.target.value }))} />}
        {validationMessage(failure, `recipe.${field}`) && <span className="field-error">{validationMessage(failure, `recipe.${field}`)}</span>}
      </label>)}
      {failure !== null && <Notice tone="error">{failure instanceof Error ? failure.message : 'Unable to save the build recipe.'}</Notice>}
      {canManage && <div className="row-actions"><Button type="submit" variant="primary" disabled={!dirty || busy || conflict}>Save build recipe</Button>
        <Button disabled={!dirty || busy} onClick={() => setDiscarding(true)}>Discard recipe changes</Button></div>}
    </fieldset></form>
    {discarding && <ConfirmationDialog title="Discard build recipe changes?" confirmLabel="Discard changes" onCancel={() => setDiscarding(false)} onConfirm={() => {
      const fields = fieldsFor(configuration.recipe); setFields(fields); setSaved(JSON.stringify(fields)); setServerRecipe(currentRecipe); setFailure(null); setDiscarding(false)
    }}><p>Your unsaved recipe changes will be lost.</p></ConfirmationDialog>}
    {reviewing && <ConfirmationDialog title="Review the saved build recipe" confirmLabel="Keep draft for this recipe" onCancel={() => setReviewing(false)} onConfirm={() => {
      const current = fieldsFor(configuration.recipe)
      const baseline = JSON.parse(saved) as typeof fields
      const rebased = { ...current }
      for (const field of Object.keys(fields) as Array<keyof typeof fields>) if (fields[field] !== baseline[field]) rebased[field] = fields[field]
      setFields(rebased); setSaved(JSON.stringify(current)); setServerRecipe(currentRecipe); setReviewing(false); setFailure(null)
    }}>
      <p>Current Dockerfile:</p><pre>{configuration.recipe.dockerfile}</pre><p>Your draft:</p><pre>{fields.dockerfile}</pre>
      <p>Keeping the draft authorizes replacing the current recipe on the next Save.</p>
    </ConfirmationDialog>}
  </details>
}
