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
  return (
    <div className="top-band">
      <label className="tenant-selector">
        <span className="visually-hidden">Tenant</span>
        <select
          className="tenant-selector__control"
          disabled={status === 'loading' || tenants.length === 0}
          value={activeTenantId}
          onChange={(event) => onSelectTenant(event.target.value)}
        >
          {tenants.length > 0 && !activeTenantId && (
            <option value="" disabled>
              Select a tenant
            </option>
          )}
          {tenants.length === 0 ? (
            <option value="">
              {status === 'loading' ? 'Loading tenants…' : 'No tenants available'}
            </option>
          ) : (
            tenants.map((tenant) => (
              <option key={tenant.id} value={tenant.id}>
                {tenant.name} · {tenant.role}
                {tenant.isDefault ? ' · Default' : ''}
              </option>
            ))
          )}
        </select>
      </label>
      {status === 'error' && (
        <p className="top-band__error" role="alert">
          {error || 'Tenant list unavailable.'}
        </p>
      )}
    </div>
  )
}
