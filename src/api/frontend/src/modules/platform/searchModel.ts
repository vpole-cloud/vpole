import type { PaginatedResponse, SearchHit } from '../../types'
export interface SearchPages { identity: string; pages: Record<number, PaginatedResponse<SearchHit>> }
export function accumulateSearchPage(state: SearchPages, action: { identity: string; page: PaginatedResponse<SearchHit> }): SearchPages {
  return { identity: action.identity, pages: { ...(state.identity === action.identity ? state.pages : {}), [action.page.pageNumber]: action.page } }
}
export function combinedSearchPage(state: SearchPages, identity: string): PaginatedResponse<SearchHit> | null {
  if (state.identity !== identity) return null
  const pages = Object.values(state.pages).sort((a, b) => a.pageNumber - b.pageNumber)
  const last = pages.at(-1)
  if (!last) return null
  const items = new Map(pages.flatMap((page) => page.items).map((hit) => [`${hit.resourceTypeId}:${hit.sourceId}`, hit]))
  return { ...last, items: [...items.values()] }
}
