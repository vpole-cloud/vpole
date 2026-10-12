import { useCallback, useEffect, useState } from 'react'
import { fetchIsolationScopes } from '../../api'
import { useApiScope } from '../../data/useApiScope'
import { useWebServiceCreation } from './useWebServiceCreation'
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
  const { api, notifications, navigation } = useAppServices()
  const { credential } = useApiScope(api)
  const loadIsolationScopes = useCallback((groupId: string, signal?: AbortSignal) =>
    fetchIsolationScopes(api.baseUrl, groupId, credential(), api.tenantId, signal), [api.baseUrl, credential, api.tenantId])
  const manager = useWebServicesManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId,
    enabled: false, refreshAfterMutation: false })
  const [dirty, setDirty] = useState(false)
  const [mode, setMode] = useState<'image' | 'git'>('image')
  const [gitVisited, setGitVisited] = useState(false)
  const [source, setSource] = useState<SourceRepository | null>(null)
  const creation = useWebServiceCreation((id, mode) => {
    setDirty(false)
    notifications.publish(mode === 'git' ? 'Git build queued. Inspect Builds for progress and Deployments for reconciliation results.' :
      'Service configuration accepted. Each deployment reconciles independently.')
    onCreated(id, true, mode === 'git' ? 'builds' : undefined)
  })
  const createdId = creation.accepted.serviceId
  const deliveryError = creation.error instanceof Error ? creation.error.message : ''
  const hasDraft = !createdId && (dirty || Boolean(source))
  useUnsavedChanges(hasDraft, 'New service')
  useEffect(() => {
    onDirtyChange(createId, hasDraft)
    return () => onDirtyChange(createId, false)
  }, [hasDraft, onDirtyChange])
  return <div className="web-service-editor">
    <section className="panel delivery-choice"><div><p className="panel__eyebrow">Deployment source</p><h1>Create a web service</h1></div>
      <div className="row-actions"><Button aria-pressed={mode === 'image'} disabled={creation.locked || creation.busy} onClick={() => setMode('image')}>Container image</Button>
        <Button aria-pressed={mode === 'git'} disabled={creation.locked || creation.busy} onClick={() => { setMode('git'); setGitVisited(true) }}>Git repository</Button></div>
      <p className="field__hint">{mode === 'image' ? 'Deploy an existing container image.' : 'Choose a source, configure the service, then build and deploy from Git.'}</p></section>
    {gitVisited && <div hidden={mode !== 'git'}><GitSourcePicker source={source} onSelect={setSource} disabled={creation.locked || creation.busy} /></div>}
    {groupsError && <Notice tone="error">{groupsError} {onRefreshGroups && <Button onClick={() => void onRefreshGroups()}>Retry groups</Button>}</Notice>}
    {creation.locked ? <section className="panel"><h2 className="panel__title">{createdId ? 'Service created' : 'Service creation'}</h2>
      <p>{createdId ? 'The initial image configuration was accepted. Source setup and build queueing are separate steps.' : 'Waiting to confirm the service creation result. The submitted source and configuration are retained.'}</p>
      {creation.busy && <p role="status">Configuring source and queueing the build…</p>}
      {deliveryError && <Notice tone="error">{deliveryError} {createdId ? 'Your service is retained.' : 'The creation request will not be repeated.'}</Notice>}
      <div className="row-actions">{creation.state.kind === 'recoverable-failure' && <Button variant="primary" disabled={creation.busy} onClick={() => void creation.retry().catch(() => {})}>{createdId ? 'Retry source setup' : 'Inspect creation result'}</Button>}
        {createdId && <Button disabled={creation.busy} onClick={() => onCreated(createdId, true, 'builds')}>Open service builds</Button>}</div>
    </section> : null}
    <div hidden={creation.locked}>{groups.length === 0 && groupsStatus !== 'success' ? <EmptyState title={groupsStatus === 'error' ? 'Unable to load resource groups.' : 'Loading resource groups…'} error={groupsStatus === 'error'} /> : groups.length === 0 ? <EmptyState title="Create a resource group first.">
      <p>A group provides the isolation and placement defaults for this service.</p><Button onClick={() => navigation.openResource({ resourceTypeId: 'pole/resource-group', resourceId: 'create' })}>Create resource group</Button>
    </EmptyState> : <WebServiceForm mode="create" resourceGroups={groups}
    onLoadIsolationScopes={loadIsolationScopes}
    availableTargets={manager.availableTargets} targetsStatus={manager.targetsStatus} targetsError={manager.targetsError}
    computeProfilesByTarget={manager.computeProfilesByTarget} computeProfilesStatusByTarget={manager.computeProfilesStatusByTarget} onLoadTargets={manager.loadAvailableTargets}
    onLoadComputeProfiles={manager.loadComputeProfiles} submitStatus={creation.busy ? 'loading' : creation.error ? 'error' : 'idle'} submitError={deliveryError}
    submitLabel={mode === 'git' ? 'Create service and build' : undefined} submitDisabled={mode === 'git' && !source}
    imageHint={mode === 'git' ? 'Initial image: this runs until the first Git build is applied. The build replaces it. Use a suitable starter image and configure ports for your application.' : undefined}
    onDirtyChange={setDirty} onSubmit={(input) => creation.submit(input, mode, source?.id)} />}</div></div>
}
