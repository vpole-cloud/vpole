// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export const domainZoneHref = (id = 'catalog', tenantId?: string) => {
  const query = new URLSearchParams({ view: 'domains', resource: id, section: 'overview' })
  if (tenantId) query.set('tenant', tenantId)
  return `?${query}`
}
