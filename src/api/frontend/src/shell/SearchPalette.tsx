import { useEffect, useId, useRef, useState } from 'react'
import { useResourceSearch } from '../modules/platform/useResourceSearch'
import { Button, Notice } from '../ui/components'
import { useAppServices } from './AppContext'
import type { AppRegistry } from './contracts'

export function SearchPalette({
  registry,
  onClose,
}: {
  registry: AppRegistry
  onClose: () => void
}) {
  const services = useAppServices()
  const { navigation, resourceTypes } = services
  const dialog = useRef<HTMLDialogElement>(null)
  const input = useRef<HTMLInputElement>(null)
  const titleId = useId()
  const listId = useId()
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState(0)
  const [pageNumber, setPageNumber] = useState(1)
  const { result, key, hits, status, error, refresh } = useResourceSearch(query, pageNumber)
  const commands = registry.commands.filter(
    (command) =>
      (command.available?.({
        auth: services.auth,
        tenant: services.tenant,
        resourceTypes,
        location: { viewId: navigation.viewId, search: navigation.search },
      }) ??
        true) &&
      command.label.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()),
  )
  const views = registry.views.filter(
    (view) =>
      (view.access !== 'tenant' || services.tenant.active) &&
      view.label.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()),
  )
  const items = [
    ...hits.map((hit) => ({
      id: `resource:${hit.resourceTypeId}:${hit.sourceId}`,
      label: hit.title,
      detail: `${resourceTypes.find((type) => type.id === hit.resourceTypeId)?.label ?? hit.sourceType} · ${hit.resourceGroupName ?? 'Tenant resource'}${resourceTypes.some((type) => type.id === hit.resourceTypeId) ? '' : ' · UI unavailable'}`,
      run: () =>
        navigation.openResource({ resourceTypeId: hit.resourceTypeId, resourceId: hit.sourceId }),
    })),
    ...commands.map((command) => ({
      id: `command:${command.id}`,
      label: command.label,
      detail: 'Action',
      run: () => command.run(services),
    })),
    ...views.map((view) => ({
      id: `view:${view.id}`,
      label: `Open ${view.label.toLocaleLowerCase()}`,
      detail: 'View',
      run: () => navigation.goTo(view.id),
    })),
  ]
  const active = Math.min(selected, Math.max(0, items.length - 1))
  const activate = (index: number) => {
    const item = items[index]
    if (item) {
      onClose()
      item.run()
    }
  }

  useEffect(() => {
    const element = dialog.current!
    const previousFocus = document.activeElement as HTMLElement | null
    element.showModal()
    input.current?.focus()
    return () => {
      element.close()
      previousFocus?.focus()
    }
  }, [])
  useEffect(() => {
    document.getElementById(`${listId}-${active}`)?.scrollIntoView?.({ block: 'nearest' })
  }, [active, listId])

  return (
    <dialog
      className="ui-dialog search-palette"
      ref={dialog}
      aria-labelledby={titleId}
      onCancel={(event) => {
        event.preventDefault()
        onClose()
      }}
    >
      <div className="search-palette__header">
        <h2 id={titleId}>Search and commands</h2>
        <Button onClick={onClose} aria-label="Close search">
          Close
        </Button>
      </div>
      <label className="field">
        <span className="field__label">Find a resource or action</span>
        <input
          ref={input}
          className="field__control"
          role="combobox"
          aria-autocomplete="list"
          aria-expanded="true"
          aria-controls={listId}
          aria-activedescendant={items.length ? `${listId}-${active}` : undefined}
          value={query}
          placeholder="Search in the active tenant…"
          onChange={(event) => {
            setQuery(event.target.value)
            setSelected(0)
            setPageNumber(1)
          }}
          onKeyDown={(event) => {
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
              event.preventDefault()
              setSelected(
                items.length
                  ? (active + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length
                  : 0,
              )
            } else if (event.key === 'Enter') {
              event.preventDefault()
              activate(active)
            }
          }}
        />
      </label>
      <p className="field__hint">
        {services.tenant.active?.name} · ↑ ↓ to select · Enter to open · Esc to close
      </p>
      {query.trim() && status === 'loading' && <p role="status">Searching…</p>}
      {error && (
        <Notice tone="error">
          {error} <Button onClick={() => void refresh()}>Retry search</Button>
        </Notice>
      )}
      {query.trim() && status === 'success' && hits.length === 0 && (
        <p role="status">No matching resources.</p>
      )}
      {!query.trim() && (
        <p className="field__hint">Type a resource name to search, or choose an action below.</p>
      )}
      <div
        id={listId}
        role="listbox"
        aria-label="Resources and commands"
        className="search-palette__results"
      >
        {items.map((item, index) => (
          <button
            type="button"
            role="option"
            id={`${listId}-${index}`}
            key={item.id}
            aria-selected={index === active}
            className={`search-palette__result${index === active ? ' is-selected' : ''}`}
            onClick={() => activate(index)}
            onFocus={() => setSelected(index)}
          >
            <strong>{item.label}</strong>
            <span>{item.detail}</span>
          </button>
        ))}
      </div>
      {result?.key === key && result.page.items.length < result.page.totalCount && (
        <Button
          disabled={status !== 'success'}
          onClick={() => setPageNumber(result.page.pageNumber + 1)}
        >
          More results
        </Button>
      )}
    </dialog>
  )
}
