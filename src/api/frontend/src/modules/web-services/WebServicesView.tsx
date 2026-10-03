import { useState } from 'react'
import { WebServicesPage } from '../../components/WebServicesPage'
import { useDomainZonesManager } from '../../hooks/useDomainZonesManager'
import { useResourceGroupsManager } from '../../hooks/useResourceGroupsManager'
import { useSourceBuildsManager } from '../../hooks/useSourceBuildsManager'
import { useWebServicesManager } from '../../hooks/useWebServicesManager'
import { useConsoleServices } from '../../console/ConsoleContext'

export function WebServicesView() {
  const { api, tenant, notifications } = useConsoleServices()
  const options = { apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: Boolean(tenant.active) }
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const groups = useResourceGroupsManager(options)
  const services = useWebServicesManager(options)
  const zones = useDomainZonesManager(options)
  const builds = useSourceBuildsManager({ ...options, webServiceId: selectedId })

  return <WebServicesPage items={services.items} resourceGroups={groups.items}
    status={services.status} error={services.error} submitStatus={services.submitStatus}
    submitError={services.submitError} deletingId={services.deletingId}
    onRefresh={services.refresh} onCreate={async (input) => {
      const created = await services.create(input)
      setSelectedId(created.webServiceId)
      notifications.publish('Service configuration accepted. Each deployment reconciles independently.')
    }} onUpdate={services.update} onDelete={services.remove}
    selectedId={selectedId} onSelect={setSelectedId} sourceBuilds={builds} canManage={tenant.canManage}
    availableTargets={services.availableTargets} targetsStatus={services.targetsStatus}
    targetsError={services.targetsError} computeProfilesByTarget={services.computeProfilesByTarget}
    zones={zones.items} onLoadTargets={services.loadAvailableTargets} onLoadComputeProfiles={services.loadComputeProfiles}
    onLoadHostnameBindings={services.loadHostnameBindings} onLoadReleases={services.loadReleases}
    hostnameBindingsByDeployment={services.hostnameBindingsByDeployment} releasesByWebService={services.releasesByWebService}
    onAddDeployment={services.addDeployment} onUpdateDeployment={services.updateDeployment}
    onRemoveDeployment={services.removeDeployment} onRetryDeployment={services.retryDeployment}
    onAddHostname={services.addHostname} onRemoveHostname={services.removeHostname} />
}

