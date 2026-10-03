import { useState } from 'react'
import { Button, ConfirmationDialog, DataTable, EmptyState, Field, StatusBadge } from '../console/components'
import { WebServiceForm } from './WebServiceForm'
import { WebServiceDeploymentsPanel } from './WebServiceDeploymentsPanel'
import { SourceBuildPanel } from './SourceBuildPanel'
import type { SourceBuildsManager } from '../hooks/useSourceBuildsManager'
import type {
  LoadStatus,
  ResourceGroup,
  TenantDomainZone,
  WebService,
  WebServiceInput,
} from '../types'

interface WebServicesPageProps {
  items: WebService[]
  resourceGroups: ResourceGroup[]
  status: LoadStatus
  error?: string
  submitStatus: LoadStatus
  submitError?: string
  deletingId: string | null
  onRefresh: () => Promise<void>
  onCreate: (input: WebServiceInput) => Promise<void>
  onUpdate: (id: string, input: WebServiceInput) => Promise<void>
  onDelete: (id: string) => Promise<void>
  selectedId: string | null
  onSelect: (id: string | null) => void
  sourceBuilds: SourceBuildsManager
  canManage?: boolean
  availableTargets: Parameters<typeof WebServiceForm>[0]['availableTargets']
  targetsStatus: Parameters<typeof WebServiceForm>[0]['targetsStatus']
  targetsError?: string
  computeProfilesByTarget: Parameters<typeof WebServiceForm>[0]['computeProfilesByTarget']
  zones: TenantDomainZone[]
  onLoadTargets: Parameters<typeof WebServiceForm>[0]['onLoadTargets']
  onLoadComputeProfiles: Parameters<typeof WebServiceForm>[0]['onLoadComputeProfiles']
  onLoadHostnameBindings: Parameters<typeof WebServiceDeploymentsPanel>[0]['onLoadHostnameBindings']
  onLoadReleases: Parameters<typeof WebServiceDeploymentsPanel>[0]['onLoadReleases']
  hostnameBindingsByDeployment: Parameters<typeof WebServiceDeploymentsPanel>[0]['hostnameBindingsByDeployment']
  releasesByWebService: Record<string, import('../types').WebServiceReleaseHistory>
  onAddDeployment: Parameters<typeof WebServiceDeploymentsPanel>[0]['onAddDeployment']
  onUpdateDeployment: Parameters<typeof WebServiceDeploymentsPanel>[0]['onUpdateDeployment']
  onRemoveDeployment: Parameters<typeof WebServiceDeploymentsPanel>[0]['onRemoveDeployment']
  onRetryDeployment: Parameters<typeof WebServiceDeploymentsPanel>[0]['onRetryDeployment']
  onAddHostname: Parameters<typeof WebServiceDeploymentsPanel>[0]['onAddHostname']
  onRemoveHostname: Parameters<typeof WebServiceDeploymentsPanel>[0]['onRemoveHostname']
}

export function WebServicesPage({
  items,
  resourceGroups,
  status,
  error,
  submitStatus,
  submitError,
  deletingId,
  onRefresh,
  onCreate,
  onUpdate,
  onDelete,
  selectedId,
  onSelect,
  sourceBuilds,
  canManage = true,
  availableTargets,
  targetsStatus,
  targetsError,
  computeProfilesByTarget,
  zones,
  onLoadTargets,
  onLoadComputeProfiles,
  onLoadHostnameBindings,
  onLoadReleases,
  hostnameBindingsByDeployment,
  releasesByWebService,
  onAddDeployment,
  onUpdateDeployment,
  onRemoveDeployment,
  onRetryDeployment,
  onAddHostname,
  onRemoveHostname,
}: WebServicesPageProps) {
  const [query, setQuery] = useState('')
  const [deleteCandidate, setDeleteCandidate] = useState<WebService | null>(null)

  const filteredItems = items.filter((item) =>
    [
      item.name,
      ...item.deployments.flatMap((deployment) => deployment.ingress?.hostnames ?? []),
      item.spec.image.server,
      item.spec.image.name,
    ]
      .join(' ')
      .toLowerCase()
      .includes(query.trim().toLowerCase()),
  )

  const selectedService =
    filteredItems.find((item) => item.id === selectedId) ??
    items.find((item) => item.id === selectedId) ??
    null

  const resolveResourceGroupName = (resourceGroupId: string) =>
    resourceGroups.find((group) => group.id === resourceGroupId)?.name ?? 'Unknown group'

  const handleDelete = async () => {
    if (!deleteCandidate) return
    try {
      await onDelete(deleteCandidate.id)
      if (selectedId === deleteCandidate.id) onSelect(null)
      setDeleteCandidate(null)
    } catch {
      // The manager preserves the concrete API error; leave the dialog available for retry.
    }
  }

  return (
    <div className="resource-groups-page">
      <header className="console-page-header">
        <div><h1>Web services</h1><p>Deploy an image and manage its configuration and targets.</p></div>
        <div className="row-actions">
          <Button aria-label="Refresh web services" onClick={() => void onRefresh()}>Refresh</Button>
          <Button variant="primary" disabled={!canManage} onClick={() => onSelect(null)}>New service</Button>
        </div>
      </header>

      <div className="resource-groups-layout resource-groups-layout--wide-form">
        <section className="panel resource-groups-list-panel">
          <div className="panel__header">
            <div>
              <p className="panel__eyebrow">Runtime</p>
              <h2 className="panel__title">All web services</h2>
            </div>
            <span className="panel__meta">{filteredItems.length} visible</span>
          </div>

          <Field label="Search">
            <input className="field__control" value={query} onChange={(event) => setQuery(event.target.value)}
              placeholder="Find by name, domain, or image" />
          </Field>
          {status === 'loading' && <EmptyState title="Loading web services…" />}
          {status === 'error' && <EmptyState title="Unable to load web services." error>{error}</EmptyState>}
          {status === 'success' && filteredItems.length === 0 && <EmptyState
            title={items.length === 0 ? 'No web services yet.' : 'No matching services.'}>
            {items.length === 0 && 'Create your first service using a container image.'}
          </EmptyState>}
          {status === 'success' && filteredItems.length > 0 && <DataTable label="Web service inventory"
            items={filteredItems} itemKey={(item) => item.id} selectedKey={selectedId} columns={[
              { id: 'name', label: 'Service', render: (item) => <><Button className="console-resource-link"
                onClick={() => onSelect(item.id)}>{item.name}</Button><small>{resolveResourceGroupName(item.resourceGroupId)}</small></> },
              { id: 'image', label: 'Image', render: (item) => <code>{item.spec.image.server}/{item.spec.image.name}:{item.spec.image.tag}</code> },
              { id: 'placement', label: 'Deployments', render: (item) => <><StatusBadge tone="info">
                {item.deployments.length} target{item.deployments.length === 1 ? '' : 's'}</StatusBadge>
                <small>{item.exposure.mode}</small></> },
              { id: 'actions', label: 'Actions', render: (item) => <div className="row-actions">
                <Button onClick={() => onSelect(item.id)}>{canManage ? 'Edit' : 'Inspect'}</Button>
                <Button variant="danger" disabled={deletingId === item.id || !canManage}
                  aria-label={`Delete ${item.name}`} onClick={() => setDeleteCandidate(item)}>Delete</Button>
              </div> },
            ]} />}

        </section>

        {canManage ? (
          <WebServiceForm
            key={selectedService?.id ?? 'create'}
            mode={selectedService ? 'edit' : 'create'}
            resourceGroups={resourceGroups}
            initialValue={selectedService}
            availableTargets={availableTargets}
            targetsStatus={targetsStatus}
            targetsError={targetsError}
            computeProfilesByTarget={computeProfilesByTarget}
            submitStatus={submitStatus}
            submitError={submitError}
            onLoadTargets={onLoadTargets}
            onLoadComputeProfiles={onLoadComputeProfiles}
            onSubmit={async (input) => {
              if (selectedService) {
                await onUpdate(selectedService.id, input)
                return
              }

              await onCreate(input)
            }}
            onCancelEdit={selectedService ? () => onSelect(null) : undefined}
          />
        ) : (
          <section className="panel">
            <div className="empty-state">Tenant viewer access is read-only.</div>
          </section>
        )}
      </div>
      {selectedService && (
        <WebServiceDeploymentsPanel
          key={`deployments-${selectedService.id}`}
          webService={selectedService}
          availableTargets={availableTargets}
          computeProfilesByTarget={computeProfilesByTarget}
          hostnameBindingsByDeployment={hostnameBindingsByDeployment}
          releases={releasesByWebService[selectedService.id]}
          zones={zones}
          canManage={canManage}
          onLoadComputeProfiles={onLoadComputeProfiles}
          onLoadHostnameBindings={onLoadHostnameBindings}
          onLoadReleases={onLoadReleases}
          onAddDeployment={onAddDeployment}
          onUpdateDeployment={onUpdateDeployment}
          onRemoveDeployment={onRemoveDeployment}
          onRetryDeployment={onRetryDeployment}
          onAddHostname={onAddHostname}
          onRemoveHostname={onRemoveHostname}
        />
      )}
      {selectedService && (
        <SourceBuildPanel
          key={selectedService.id}
          webService={selectedService}
          manager={sourceBuilds}
          canManage={canManage}
        />
      )}
      {deleteCandidate && <ConfirmationDialog title={`Delete ${deleteCandidate.name}?`}
        confirmLabel="Delete service" busy={deletingId === deleteCandidate.id}
        onCancel={() => setDeleteCandidate(null)} onConfirm={() => { void handleDelete() }}>
        <p>This requests deletion of the service and its deployments.</p>
        {submitError && <EmptyState title="Deletion failed." error>{submitError}</EmptyState>}
      </ConfirmationDialog>}
    </div>
  )
}
