import { useState } from 'react'
import { Button, Field } from './components'

export function InventorySearch({
  search,
  placeholder,
  onSearch,
}: {
  search: string
  placeholder: string
  onSearch: (search: string) => void
}) {
  const [draft, setDraft] = useState(search)
  const [currentSearch, setCurrentSearch] = useState(search)
  if (currentSearch !== search) {
    setCurrentSearch(search)
    setDraft(search)
  }
  return (
    <form
      className="inventory-search"
      role="search"
      onSubmit={(event) => {
        event.preventDefault()
        onSearch(draft.trim())
      }}
    >
      <Field label="Search">
        <input
          className="field__control"
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
          placeholder={placeholder}
        />
      </Field>
      <Button type="submit">Search</Button>
    </form>
  )
}

export function Pagination({
  label,
  page,
  pageNumber,
  pageSize,
  onPage,
  onPageSize,
  compact = false,
}: {
  label: string
  page: { totalCount: number; pageNumber: number; pageSize: number } | null
  pageNumber: number
  pageSize: number
  onPage: (page: number) => void
  onPageSize: (size: number) => void
  compact?: boolean
}) {
  const totalPages = Math.max(1, Math.ceil((page?.totalCount ?? 0) / pageSize))
  const loadedPages = page ? Math.max(1, Math.ceil(page.totalCount / page.pageSize)) : totalPages
  return (
    <nav
      className={`inventory-pagination${compact ? ' inventory-pagination--compact' : ''}`}
      aria-label={`${label} pages`}
    >
      <Field label="Per page">
        <select
          className="field__control"
          aria-label={`${label} per page`}
          value={pageSize}
          onChange={(event) => onPageSize(Number(event.target.value))}
        >
          {[...new Set([8, 20, 50, pageSize])]
            .sort((a, b) => a - b)
            .map((size) => (
              <option key={size} value={size}>
                {size}
              </option>
            ))}
        </select>
      </Field>
      <span aria-live="polite">{`Page ${page?.pageNumber ?? pageNumber} of ${loadedPages}`}</span>
      <div className="row-actions">
        <Button
          aria-label="Previous page"
          disabled={pageNumber <= 1}
          onClick={() => onPage(pageNumber - 1)}
        >
          {compact ? '‹' : 'Previous page'}
        </Button>
        <Button
          aria-label="Next page"
          disabled={!page || pageNumber >= totalPages}
          onClick={() => onPage(pageNumber + 1)}
        >
          {compact ? '›' : 'Next page'}
        </Button>
      </div>
    </nav>
  )
}
