export const domainZoneHref = (id = 'catalog', tenantId?: string) => {
  const query = new URLSearchParams({ view: 'domains', resource: id, section: 'overview' })
  if (tenantId) query.set('tenant', tenantId)
  return `?${query}`
}
