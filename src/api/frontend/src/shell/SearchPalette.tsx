import { useEffect, useId, useRef, useState } from 'react'
import { searchResources } from '../api'
import type { LoadStatus, PaginatedResponse, SearchHit } from '../types'
import { Button, Notice } from '../ui/components'
import { useAppServices } from './AppContext'
import type { AppRegistry } from './contracts'

export function SearchPalette({ registry, onClose }: { registry: AppRegistry; onClose: () => void }) {
  const services = useAppServices()
  const { api, navigation, resourceTypes } = services
  const dialog = useRef<HTMLDialogElement>(null)
  const input = useRef<HTMLInputElement>(null)
  const titleId = useId()
  const listId = useId()
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState(0)
  const [pageNumber, setPageNumber] = useState(1)
  const [result, setResult] = useState<{ key: string; page: PaginatedResponse<SearchHit> } | null>(null)
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [retry, setRetry] = useState(0)
  const key = `${api.baseUrl}:${api.tenantId}:${api.token}:${query.trim()}`
  const hits = result?.key === key ? result.page.items : []
  const commands = registry.commands.filter((command) => (command.available?.({ auth: services.auth, tenant: services.tenant,
    resourceTypes, location: { viewId: navigation.viewId, search: navigation.search } }) ?? true) &&
    command.label.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()))
  const views = registry.views.filter((view) => (view.access !== 'tenant' || services.tenant.active) &&
    view.label.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()))
  const items = [
    ...hits.map((hit) => ({ id: `resource:${hit.resourceTypeId}:${hit.sourceId}`, label: hit.title,
      detail: `${resourceTypes.find((type) => type.id === hit.resourceTypeId)?.label ?? hit.sourceType} · ${hit.resourceGroupName ?? 'Tenant resource'}${resourceTypes.some((type) => type.id === hit.resourceTypeId) ? '' : ' · UI unavailable'}`,
      run: () => navigation.openResource({ resourceTypeId: hit.resourceTypeId, resourceId: hit.sourceId }) })),
    ...commands.map((command) => ({ id: `command:${command.id}`, label: command.label, detail: 'Action', run: () => command.run(services) })),
    ...views.map((view) => ({ id: `view:${view.id}`, label: `Open ${view.label.toLocaleLowerCase()}`, detail: 'View', run: () => navigation.goTo(view.id) })),
  ]
  const active = Math.min(selected, Math.max(0, items.length - 1))
  const activate = (index: number) => { const item = items[index]; if (item) { onClose(); item.run() } }

  useEffect(() => {
    const element = dialog.current!
    const previousFocus = document.activeElement as HTMLElement | null
    element.showModal()
    input.current?.focus()
    return () => { element.close(); previousFocus?.focus() }
  }, [])
  useEffect(() => { document.getElementById(`${listId}-${active}`)?.scrollIntoView?.({ block: 'nearest' }) }, [active, listId])
  useEffect(() => {
    if (!query.trim() || !api.tenantId) return
    const controller = new AbortController()
    const timer = window.setTimeout(async () => {
      setStatus('loading')
      setError('')
      try {
        const page = await searchResources(api.baseUrl, query, api.token, api.tenantId, controller.signal, pageNumber)
        if (controller.signal.aborted) return
        setResult((current) => {
          const items = pageNumber > 1 && current?.key === key ? [...current.page.items, ...page.items] : page.items
          const unique = new Map(items.map((hit) => [`${hit.resourceTypeId}:${hit.sourceId}`, hit]))
          return { key, page: { ...page, items: [...unique.values()] } }
        })
        setStatus('success')
      } catch (failure) {
        if (controller.signal.aborted) return
        setStatus('error')
        setError(failure instanceof Error ? failure.message : 'Unable to search resources.')
      }
    }, 200)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [api.baseUrl, api.tenantId, api.token, query, key, pageNumber, retry])

  return <dialog className="ui-dialog search-palette" ref={dialog} aria-labelledby={titleId}
    onCancel={(event) => { event.preventDefault(); onClose() }}>
    <div className="search-palette__header"><h2 id={titleId}>Search and commands</h2><Button onClick={onClose} aria-label="Close search">Close</Button></div>
    <label className="field"><span className="field__label">Find a resource or action</span>
      <input ref={input} className="field__control" role="combobox" aria-autocomplete="list" aria-expanded="true"
        aria-controls={listId} aria-activedescendant={items.length ? `${listId}-${active}` : undefined}
        value={query} placeholder="Search in the active tenant…" onChange={(event) => {
          setQuery(event.target.value); setSelected(0); setPageNumber(1); setStatus('idle'); setError('')
        }} onKeyDown={(event) => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault()
            setSelected(items.length ? (active + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length : 0)
          } else if (event.key === 'Enter') { event.preventDefault(); activate(active) }
        }} />
    </label>
    <p className="field__hint">{services.tenant.active?.name} · ↑ ↓ to select · Enter to open · Esc to close</p>
    {query.trim() && status === 'loading' && <p role="status">Searching…</p>}
    {error && <Notice tone="error">{error} <Button onClick={() => setRetry((value) => value + 1)}>Retry search</Button></Notice>}
    {query.trim() && status === 'success' && hits.length === 0 && <p role="status">No matching resources.</p>}
    {!query.trim() && <p className="field__hint">Type a resource name to search, or choose an action below.</p>}
    <div id={listId} role="listbox" aria-label="Resources and commands" className="search-palette__results">
      {items.map((item, index) => <button type="button" role="option" id={`${listId}-${index}`} key={item.id}
        aria-selected={index === active} className={`search-palette__result${index === active ? ' is-selected' : ''}`}
        onClick={() => activate(index)} onFocus={() => setSelected(index)}>
        <strong>{item.label}</strong><span>{item.detail}</span></button>)}
    </div>
    {result?.key === key && result.page.items.length < result.page.totalCount && <Button
      disabled={status !== 'success'} onClick={() => setPageNumber(result.page.pageNumber + 1)}>More results</Button>}
  </dialog>
}
