import { useCallback, useEffect, useRef, useState } from 'react'
import { createSourceBuildConfiguration, fetchIsolationScopes, fetchSourceBuildConfigurations, fetchSourceBuildJobs, startSourceBuild } from '../../api'
import { WebServiceForm } from '../../components/WebServiceForm'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { useWebServicesManager } from '../../hooks/useWebServicesManager'
import type { LoadStatus, ResourceGroup, SourceRepository } from '../../types'
import { Button, EmptyState, Notice } from '../../ui/components'
import { GitSourcePicker } from './GitSourcePicker'
import { createId } from './workspaceState'
import type { ServiceSection } from './workspaceState'

export function NewWebServiceView({ groups, groupsStatus = 'success', groupsError, onRefreshGroups, onCreated, onDirtyChange }:
  { groups: ResourceGroup[]; groupsStatus?: LoadStatus; groupsError?: string; onRefreshGroups?: () => Promise<unknown>;
    onCreated: (id: string, open: boolean, section?: ServiceSection) => void; onDirtyChange: (id: string, dirty: boolean) => void }) {
  const { api, notifications, navigation, invalidateResources } = useAppServices()
  const loadIsolationScopes = useCallback((groupId: string, signal?: AbortSignal) =>
    fetchIsolationScopes(api.baseUrl, groupId, api.token, api.tenantId, signal), [api.baseUrl, api.token, api.tenantId])
  const manager = useWebServicesManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId,
    enabled: false, refreshAfterMutation: false })
  const [dirty, setDirty] = useState(false)
  const [mode, setMode] = useState<'image' | 'git'>('image')
  const [gitVisited, setGitVisited] = useState(false)
  const [source, setSource] = useState<SourceRepository | null>(null)
  const [createdId, setCreatedId] = useState<string | null>(null)
  const configurationId = useRef<string | null>(null)
  const [deliveryStatus, setDeliveryStatus] = useState<'idle' | 'loading' | 'error'>('idle')
  const [deliveryError, setDeliveryError] = useState('')
  const mounted = useRef(false)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const hasDraft = dirty || Boolean(source && !createdId)
  useUnsavedChanges(hasDraft, 'New service')
  useEffect(() => {
    onDirtyChange(createId, hasDraft)
    return () => onDirtyChange(createId, false)
  }, [hasDraft, onDirtyChange])
  const continueBuild = async (id: string) => {
    if (!source || !mounted.current) return
    setDeliveryStatus('loading'); setDeliveryError('')
    try {
      // Recover an accepted configuration/queue response after a lost connection before retrying writes.
      const configurations = await fetchSourceBuildConfigurations(api.baseUrl, api.token, api.tenantId)
      if (!mounted.current) return
      const existing = configurations.find((item) => item.webServiceId === id)
      if (existing && existing.sourceRepositoryId !== source.id) throw new Error('This service already has a different source. Open Builds to inspect its configuration.')
      configurationId.current = existing?.id ?? configurationId.current
      if (!configurationId.current) {
        const configuration = await createSourceBuildConfiguration(api.baseUrl, source.id, id, api.token, api.tenantId)
        if (!mounted.current) return
        configurationId.current = configuration.id
      }
      const jobs = await fetchSourceBuildJobs(api.baseUrl, id, api.token, api.tenantId)
      if (!mounted.current) return
      if (!jobs.items.some((job) => job.configurationId === configurationId.current)) {
        await startSourceBuild(api.baseUrl, configurationId.current, api.token, api.tenantId)
        if (!mounted.current) return
      }
      notifications.publish('Git build queued. Inspect Builds for progress and Deployments for reconciliation results.')
      onCreated(id, true, 'builds')
    } catch (failure) {
      if (!mounted.current) return
      setDeliveryStatus('error'); setDeliveryError(failure instanceof Error ? failure.message : 'Unable to configure or queue the build.')
    }
  }
  return <div className="web-service-editor">
    <section className="panel delivery-choice"><div><p className="panel__eyebrow">Deployment source</p><h1>Create a web service</h1></div>
      <div className="row-actions"><Button aria-pressed={mode === 'image'} disabled={Boolean(createdId)} onClick={() => setMode('image')}>Container image</Button>
        <Button aria-pressed={mode === 'git'} disabled={Boolean(createdId)} onClick={() => { setMode('git'); setGitVisited(true) }}>Git repository</Button></div>
      <p className="field__hint">{mode === 'image' ? 'Deploy an existing container image.' : 'Choose a source, configure the service, then build and deploy from Git.'}</p></section>
    {gitVisited && <div hidden={mode !== 'git'}><GitSourcePicker source={source} onSelect={setSource} disabled={Boolean(createdId)} /></div>}
    {groupsError && <Notice tone="error">{groupsError} {onRefreshGroups && <Button onClick={() => void onRefreshGroups()}>Retry groups</Button>}</Notice>}
    {createdId ? <section className="panel"><h2 className="panel__title">Service created</h2>
      <p>The initial image configuration was accepted. Source setup and build queueing are separate steps.</p>
      {deliveryStatus === 'loading' && <p role="status">Configuring source and queueing the build…</p>}
      {deliveryError && <Notice tone="error">{deliveryError} Your service is retained.</Notice>}
      <div className="row-actions">{deliveryStatus === 'error' && <Button variant="primary" onClick={() => void continueBuild(createdId)}>Retry source setup</Button>}
        <Button disabled={deliveryStatus === 'loading'} onClick={() => onCreated(createdId, true, 'builds')}>Open service builds</Button></div>
    </section> : groups.length === 0 && groupsStatus !== 'success' ? <EmptyState title={groupsStatus === 'error' ? 'Unable to load resource groups.' : 'Loading resource groups…'} error={groupsStatus === 'error'} /> : groups.length === 0 ? <EmptyState title="Create a resource group first.">
      <p>A group provides the isolation and placement defaults for this service.</p><Button onClick={() => navigation.openResource({ resourceTypeId: 'pole/resource-group', resourceId: 'create' })}>Create resource group</Button>
    </EmptyState> : <WebServiceForm mode="create" resourceGroups={groups}
    onLoadIsolationScopes={loadIsolationScopes}
    availableTargets={manager.availableTargets} targetsStatus={manager.targetsStatus} targetsError={manager.targetsError}
    computeProfilesByTarget={manager.computeProfilesByTarget} computeProfilesStatusByTarget={manager.computeProfilesStatusByTarget} onLoadTargets={manager.loadAvailableTargets}
    onLoadComputeProfiles={manager.loadComputeProfiles} submitStatus={manager.submitStatus} submitError={manager.submitError}
    submitLabel={mode === 'git' ? 'Create service and build' : undefined} submitDisabled={mode === 'git' && !source}
    imageHint={mode === 'git' ? 'Initial image: this runs until the first Git build is applied. The build replaces it. Use a suitable starter image and configure ports for your application.' : undefined}
    onDirtyChange={setDirty} onSubmit={async (input) => {
      const created = await manager.create(input)
      invalidateResources()
      if (mode === 'git') {
        if (!mounted.current) return
        setCreatedId(created.webServiceId); setDirty(false)
        await continueBuild(created.webServiceId)
      } else {
        notifications.publish('Service configuration accepted. Each deployment reconciles independently.')
        onCreated(created.webServiceId, mounted.current)
      }
    }} />}</div>
}
