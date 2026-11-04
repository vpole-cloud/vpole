export function selectedReference(search: string, sourceId: string, kind: 'run' | 'preview') {
  const params = new URLSearchParams(search)
  return params.get('resource') === sourceId && params.get('selectionSource') === sourceId
    ? params.get(kind)
    : null
}
export function referenceParams(
  search: string,
  sourceId: string,
  kind: 'run' | 'preview',
  id: string,
) {
  const params = new URLSearchParams(search)
  params.set('resource', sourceId)
  params.set('selectionSource', sourceId)
  params.set('section', kind === 'run' ? 'runs' : 'previews')
  params.delete('run')
  params.delete('preview')
  params.set(kind, id)
  return params
}
