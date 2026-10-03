import { useEffect, useState } from 'react'
import { SourceBuildPanel } from '../../components/SourceBuildPanel'
import { WebServiceDeploymentsPanel } from '../../components/WebServiceDeploymentsPanel'
import { WebServiceForm } from '../../components/WebServiceForm'
import { useAppServices } from '../../shell/AppContext'
import { Button, ConfirmationDialog, EmptyState, Notice, StatusBadge } from '../../ui/components'
import { TabPanel, Tabs } from '../../ui/Tabs'
import { ResourceEndpoints } from '../../ui/ResourceEndpoints'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { useSourceBuildsManager } from '../../hooks/useSourceBuildsManager'
import { useWebServicesManager } from '../../hooks/useWebServicesManager'
import type { ResourceGroup, TenantDomainZone, WebService } from '../../types'
import { useWebService } from './useWebService'
import { serviceSections, type ServiceSection } from './workspaceState'

const sectionLabels: Record<ServiceSection, string> = {
  overview: 'Overview', configuration: 'Configuration', deployments: 'Deployments', builds: 'Builds',
}

export function WebServiceResourceView({ id, section, active, refreshVersion, groups, zones, onSection, onLoaded, onDirtyChange, onChanged }:
  { id: string; section: ServiceSection; active: boolean; refreshVersion: number; groups: ResourceGroup[]; zones: TenantDomainZone[];
    onSection: (id: string, section: ServiceSection) => void; onLoaded: (service: WebService) => void;
    onDirtyChange: (id: string, dirty: boolean) => void; onChanged: () => void }) {
  const { api, tenant, notifications } = useAppServices()
  const details = useWebService(id, api, refreshVersion)
  const manager = useWebServicesManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId,
    enabled: false, refreshAfterMutation: false })
  const builds = useSourceBuildsManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId,
    webServiceId: id, enabled: active && section === 'builds' })
  const [configurationDirty, setConfigurationDirty] = useState(false)
  const [deploymentDirty, setDeploymentDirty] = useState(false)
  const [buildDirty, setBuildDirty] = useState(false)
  const dirty = configurationDirty || deploymentDirty || buildDirty
  const [formVersion, setFormVersion] = useState(0)
  const [saving, setSaving] = useState(false)
  const [discarding, setDiscarding] = useState(false)
  const [visited, setVisited] = useState<ServiceSection[]>([section])
  // Keep a section mounted after its first visit, including externally selected sections.
  if (!visited.includes(section)) setVisited([...visited, section])
  const service = details.service
  const refreshDetails = details.refresh
  const completedBuild = builds.jobs.filter((job) => ['succeeded', 'failed'].includes(job.state.status)).map((job) => `${job.id}:${job.state.status}`).join(',')
  useEffect(() => { if (completedBuild) void refreshDetails() }, [completedBuild, refreshDetails])
  const reconciling = service?.deployments.some((deployment) => ['PendingApply', 'Applying'].includes(deployment.status)) ?? false
  useEffect(() => {
    if (!active || !reconciling) return
    const timer = window.setInterval(() => void refreshDetails(), 2500)
    return () => window.clearInterval(timer)
  }, [active, reconciling, refreshDetails])
  useUnsavedChanges(dirty, service?.name ?? 'Web service')
  useEffect(() => {
    onDirtyChange(id, dirty)
    return () => onDirtyChange(id, false)
  }, [dirty, id, onDirtyChange])
  useEffect(() => { if (service) onLoaded(service) }, [onLoaded, service])
  const isolationScopeId = service?.isolationScopeId ?? undefined
  const publicIngress = service?.exposure.mode === 'Public'
  const hasService = Boolean(service)
  const deploymentsVisited = visited.includes('deployments')
  const loadAvailableTargets = manager.loadAvailableTargets
  useEffect(() => {
    if (!hasService || !deploymentsVisited) return
    const controller = new AbortController()
    void loadAvailableTargets(isolationScopeId, publicIngress, controller.signal)
    return () => controller.abort()
  }, [isolationScopeId, publicIngress, loadAvailableTargets, hasService, deploymentsVisited])

  const mutate = async <T,>(action: () => Promise<T>) => {
    const result = await action()
    await details.refresh()
    onChanged()
    return result
  }
  if (!service) return <EmptyState title={details.status === 'error' ? 'Unable to open this service.' : 'Loading service…'}
    error={details.status === 'error'}>{details.error && <><p>{details.error}</p><Button onClick={() => void details.refresh()}>Retry</Button></>}</EmptyState>

  const panelId = `web-service-${id}`
  return <div className="web-service-resource">
    <header className="ui-page-header">
      <div><p className="ui-page-eyebrow">Web service · {groups.find((group) => group.id === service.resourceGroupId)?.name ?? 'Resource group'}</p>
        <h1>{service.name}</h1><p>{service.spec.image.server}/{service.spec.image.name}:{service.spec.image.tag}</p></div>
      <Button aria-label="Refresh service details" onClick={() => void details.refresh()} disabled={details.status === 'loading'}>Refresh</Button>
    </header>
    {details.error && <Notice tone="error">{details.error} Last fetched configuration is shown.</Notice>}
    {manager.submitError && section !== 'configuration' && <Notice tone="error">{manager.submitError}</Notice>}
    <Tabs id={panelId} label={`${service.name} sections`} tabs={serviceSections.map((value) => ({ id: value, label: sectionLabels[value] }))}
      activeId={section} onSelect={(value) => onSection(id, value as ServiceSection)} variant="section" />
    <TabPanel id={panelId} tabId="overview" activeId={section}>
      <section className="panel web-service-overview"><div className="panel__header"><h2 className="panel__title">Service overview</h2>
        <StatusBadge>{service.exposure.mode}</StatusBadge></div>
        <dl className="web-service-facts">
          <div><dt>Image</dt><dd><code>{service.spec.image.server}/{service.spec.image.name}:{service.spec.image.tag}</code></dd></div>
          <div><dt>Resource group</dt><dd>{groups.find((group) => group.id === service.resourceGroupId)?.name ?? service.resourceGroupId}</dd></div>
          <div><dt>Active revision</dt><dd>{service.activeRevision}</dd></div>
          <div><dt>Deployments</dt><dd>{service.deployments.length}</dd></div>
        </dl>
        <div className="web-service-target-summary">{service.deployments.map((deployment) => <div key={deployment.deploymentId}>
          <span>{deployment.compute.displayName} · {deployment.fixedReplicas} replica{deployment.fixedReplicas === 1 ? '' : 's'}</span>
          <StatusBadge tone={deployment.status === 'Applied' ? 'success' : deployment.status.includes('Fail') ? 'danger' : 'info'}>{deployment.status}</StatusBadge>
        </div>)}</div>
        <p className="field__hint">Deployment status reports reconciliation. Inspect targets, generations and hostnames in Deployments.</p>
      </section>
      {active && section === 'overview' && <ResourceEndpoints resourceType="pole/web-service" resourceId={id} />}
    </TabPanel>
    <TabPanel id={panelId} tabId="configuration" activeId={section}>
      {visited.includes('configuration') && (tenant.canManage ? <div className="web-service-editor"><WebServiceForm key={formVersion}
        mode="edit" initialValue={service} resourceGroups={groups} availableTargets={manager.availableTargets}
        targetsStatus={manager.targetsStatus} computeProfilesByTarget={manager.computeProfilesByTarget}
        onLoadTargets={manager.loadAvailableTargets} onLoadComputeProfiles={manager.loadComputeProfiles}
        submitStatus={saving ? 'loading' : manager.submitStatus} submitError={manager.submitError}
        onDirtyChange={setConfigurationDirty} onCancelEdit={() => setDiscarding(true)} onSubmit={async (input) => {
          setSaving(true)
          try {
            await manager.update(id, input)
            const updated = await details.refresh()
            if (updated) setFormVersion((current) => current + 1)
            onChanged()
            notifications.publish(`Configuration accepted for ${service.name}.`)
          } finally { setSaving(false) }
        }} /></div> : <EmptyState title="Tenant viewer access is read-only." />)}
    </TabPanel>
    <TabPanel id={panelId} tabId="deployments" activeId={section}>
      {visited.includes('deployments') && <WebServiceDeploymentsPanel webService={service} availableTargets={manager.availableTargets}
        computeProfilesByTarget={manager.computeProfilesByTarget} hostnameBindingsByDeployment={manager.hostnameBindingsByDeployment}
        releases={manager.releasesByWebService[id]} zones={zones} canManage={tenant.canManage} onDirtyChange={setDeploymentDirty}
        onLoadComputeProfiles={manager.loadComputeProfiles} onLoadHostnameBindings={manager.loadHostnameBindings} onLoadReleases={manager.loadReleases}
        onAddDeployment={(...args) => mutate(() => manager.addDeployment(...args))}
        onUpdateDeployment={(...args) => mutate(() => manager.updateDeployment(...args))}
        onRemoveDeployment={(...args) => mutate(() => manager.removeDeployment(...args))}
        onRetryDeployment={(...args) => mutate(() => manager.retryDeployment(...args))}
        onAddHostname={(...args) => mutate(() => manager.addHostname(...args))}
        onRemoveHostname={(...args) => mutate(() => manager.removeHostname(...args))} />}
    </TabPanel>
    <TabPanel id={panelId} tabId="builds" activeId={section}>
      {visited.includes('builds') && <SourceBuildPanel webService={service} manager={builds} canManage={tenant.canManage} onDirtyChange={setBuildDirty} />}
    </TabPanel>
    {discarding && <ConfirmationDialog title="Discard configuration changes?" confirmLabel="Discard changes"
      onCancel={() => setDiscarding(false)} onConfirm={() => {
        setFormVersion((current) => current + 1)
        setDiscarding(false)
        requestAnimationFrame(() => document.getElementById(`${panelId}-tab-configuration`)?.focus())
      }}>
      Your unsaved configuration changes will be lost.
    </ConfirmationDialog>}
  </div>
}
