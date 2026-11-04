import { Button } from '../../ui/components'
import type { Page } from './types'
export function PageControls({
  label,
  page,
  value,
  onPage,
}: {
  label: string
  page: number
  value: Pick<Page<unknown>, 'hasMore'> | null
  onPage: (page: number) => void
}) {
  return (
    <nav className="inventory-pagination" aria-label={`${label} pages`}>
      <Button disabled={page <= 1} onClick={() => onPage(page - 1)}>
        Previous {label}
      </Button>
      <span>Page {page}</span>
      <Button disabled={!value?.hasMore} onClick={() => onPage(page + 1)}>
        Next {label}
      </Button>
    </nav>
  )
}
