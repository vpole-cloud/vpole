import { useEffect, useId, useRef } from 'react'
import type { ButtonHTMLAttributes, ReactNode } from 'react'

export function Button({ variant = 'default', className = '', type = 'button', ...props }:
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'default' | 'primary' | 'danger' }) {
  return <button {...props} type={type} className={`button${variant === 'default' ? '' : ` button--${variant}`} ${className}`} />
}

export function Notice({ children, tone = 'info' }: { children: ReactNode; tone?: 'info' | 'error' | 'success' }) {
  return <div className={`console-notice console-notice--${tone}`} role={tone === 'error' ? 'alert' : 'status'}>{children}</div>
}

export function EmptyState({ title, children, error = false }: { title: string; children?: ReactNode; error?: boolean }) {
  return <div className={`empty-state${error ? ' empty-state--error' : ''}`} role={error ? 'alert' : 'status'}>
    <strong>{title}</strong>{children && <div>{children}</div>}
  </div>
}

export function StatusBadge({ children, tone = 'neutral' }:
  { children: ReactNode; tone?: 'neutral' | 'info' | 'success' | 'warning' | 'danger' }) {
  return <span className={`status-pill status-pill--${tone}`}>{children}</span>
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return <label className="field"><span className="field__label">{label}</span>{children}
    {hint && <span className="field__hint">{hint}</span>}
  </label>
}

export interface TableColumn<T> {
  id: string
  label: string
  render: (item: T) => ReactNode
}

export function DataTable<T>({ label, items, columns, itemKey, selectedKey }:
  { label: string; items: readonly T[]; columns: readonly TableColumn<T>[]; itemKey: (item: T) => string; selectedKey?: string | null }) {
  return <div className="console-table-scroll" tabIndex={0} role="region" aria-label={label}>
    <table className="console-table"><caption className="visually-hidden">{label}</caption>
      <thead><tr>{columns.map((column) => <th scope="col" key={column.id}>{column.label}</th>)}</tr></thead>
      <tbody>{items.map((item) => <tr key={itemKey(item)} className={itemKey(item) === selectedKey ? 'is-selected' : ''}>
        {columns.map((column) => <td key={column.id}>{column.render(item)}</td>)}
      </tr>)}</tbody>
    </table>
  </div>
}

export function ConfirmationDialog({ title, children, confirmLabel, busy = false, onCancel, onConfirm }:
  { title: string; children: ReactNode; confirmLabel: string; busy?: boolean; onCancel: () => void; onConfirm: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const cancel = useRef<HTMLButtonElement>(null)
  const titleId = useId()
  useEffect(() => {
    const element = dialog.current!
    const previousFocus = document.activeElement as HTMLElement | null
    element.showModal()
    cancel.current?.focus()
    return () => {
      element.close()
      previousFocus?.focus()
    }
  }, [])
  return <dialog className="console-dialog" ref={dialog} aria-labelledby={titleId} onCancel={(event) => {
    event.preventDefault()
    if (!busy) onCancel()
  }}>
    <h2 id={titleId}>{title}</h2><div>{children}</div>
    <div className="console-dialog__actions">
      <button className="button" ref={cancel} disabled={busy} onClick={onCancel} type="button">Cancel</button>
      <Button variant="danger" disabled={busy} onClick={onConfirm}>{busy ? 'Deleting…' : confirmLabel}</Button>
    </div>
  </dialog>
}
