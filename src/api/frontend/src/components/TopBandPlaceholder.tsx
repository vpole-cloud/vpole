import type { Tenant } from '../types'

interface TopBandPlaceholderProps {
  tenants: Tenant[]
  activeTenantId: string
  status: string
  error?: string
  onSelectTenant: (tenantId: string) => void
}

export function TopBandPlaceholder({
  tenants,
  activeTenantId,
  status,
  error,
  onSelectTenant,
}: TopBandPlaceholderProps) {
  const activeTenant = tenants.find((tenant) => tenant.id === activeTenantId)

  return (
    <div className="top-band">
      <div>
        <p className="top-band__label">Active tenant</p>
        <p className="top-band__hint">
          {activeTenant
            ? `${activeTenant.name} · ${activeTenant.role}`
            : status === 'error'
              ? error || 'Tenant list unavailable.'
              : 'Select a tenant to scope resource operations.'}
        </p>
      </div>
      <label className="tenant-selector">
        <span className="tenant-selector__label">Tenant</span>
        <select
          className="tenant-selector__control"
          disabled={status === 'loading' || tenants.length === 0}
          value={activeTenantId}
          onChange={(event) => onSelectTenant(event.target.value)}
        >
          {tenants.length === 0 ? (
            <option value="">No tenants</option>
          ) : (
            tenants.map((tenant) => (
              <option key={tenant.id} value={tenant.id}>
                {tenant.name}
              </option>
            ))
          )}
        </select>
      </label>
    </div>
  )
}
