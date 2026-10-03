import { Button, Field, Notice } from '../../ui/components'
import type { ValueRow } from './useWriteOnlyValues'

export function SecretValues({ rows, onChange, cleared }: { rows: ValueRow[]; onChange: (rows: ValueRow[]) => void; cleared: boolean }) {
  return <fieldset className="secret-values"><legend>Complete key/value map</legend><p className="field__hint">Values are write-only. They are cleared when you leave this form or hide the window. Empty values are allowed.</p>
    {cleared && <Notice>Values were cleared. Enter the complete map before submitting. If a request was in progress, refresh metadata before retrying.</Notice>}
    {rows.map((row, index) => <div className="secret-value-row" key={index}>
      <Field label={`Key ${index + 1}`}><input className="field__control" required maxLength={128} value={row.key} onChange={(event) => onChange(rows.map((item, i) => i === index ? { ...item, key: event.target.value } : item))} /></Field>
      <Field label={`Value ${index + 1}`}><input className="field__control" type="password" autoComplete="new-password" value={row.value} onChange={(event) => onChange(rows.map((item, i) => i === index ? { ...item, value: event.target.value, confirmed: true } : item))} /><Button onClick={() => onChange(rows.map((item, i) => i === index ? { ...item, value: '', confirmed: true } : item))}>{row.confirmed && !row.value ? 'Empty value selected' : 'Use empty value'}</Button></Field>
      <Button aria-label={`Remove key ${index + 1}`} disabled={rows.length === 1} onClick={() => onChange(rows.filter((_, i) => i !== index))}>Remove</Button>
    </div>)}<Button disabled={rows.length >= 64} onClick={() => onChange([...rows, { key: '', value: '' }])}>Add key</Button>
  </fieldset>
}
