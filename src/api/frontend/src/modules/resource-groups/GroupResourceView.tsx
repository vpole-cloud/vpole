import { useCallback, useEffect, useRef, useState } from 'react'
import { fetchResourceGroup } from '../../api'
import { ResourceGroupForm } from '../../components/ResourceGroupForm'
import { useResourceGroupsManager } from '../../hooks/useResourceGroupsManager'
import { useResource } from '../../hooks/useResource'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { Button, ConfirmationDialog, EmptyState, Notice } from '../../ui/components'
import { TabPanel, Tabs } from '../../ui/Tabs'
import type { ResourceGroup } from '../../types'
import { GroupResources } from './GroupResources'
import type { GroupSection } from './workspaceState'

export function GroupResourceView({ id, section, active, pageNumber, pageSize, refreshVersion, onSection, onPage, onLoaded, onDirtyChange, onChanged }:
  { id: string; section: GroupSection; active: boolean; pageNumber: number; pageSize: number; refreshVersion: number;
    onSection: (id: string, section: GroupSection) => void; onPage: (id: string, page: number, size: number, replace?: boolean) => void;
    onLoaded: (group: ResourceGroup) => void; onDirtyChange: (id: string, dirty: boolean) => void; onChanged: () => void }) {
  const { api, tenant, notifications } = useAppServices()
  const details = useResource(`${api.baseUrl}:${api.tenantId}:${api.token}:${id}`,
    (signal) => fetchResourceGroup(api.baseUrl, id, api.token, api.tenantId, signal), refreshVersion)
  const manager = useResourceGroupsManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: true, loadInventory: false })
  const [dirty, setDirty] = useState(false)
  const [visitedConfiguration, setVisitedConfiguration] = useState(section === 'configuration')
  if (section === 'configuration' && !visitedConfiguration) setVisitedConfiguration(true)
  const [formVersion, setFormVersion] = useState(0)
  const [discarding, setDiscarding] = useState(false)
  const group = details.resource
  useUnsavedChanges(dirty, group?.name ?? 'Resource group')
  useEffect(() => { onDirtyChange(id, dirty); return () => onDirtyChange(id, false) }, [dirty, id, onDirtyChange])
  useEffect(() => { if (group) onLoaded(group) }, [group, onLoaded])
  const changePage = useCallback((page: number, replace = false) => onPage(id, page, pageSize, replace), [id, onPage, pageSize])
  const changePageSize = useCallback((size: number) => onPage(id, 1, size), [id, onPage])
  if (!group) return <EmptyState title={details.status === 'error' ? 'Unable to open this group.' : 'Loading group…'} error={details.status === 'error'}>
    {details.error}<Button onClick={() => void details.refresh()}>Retry group</Button></EmptyState>
  const panelId = `resource-group-${id}`
  return <div className="group-resource-view">
    <header className="ui-page-header"><div><p className="ui-page-eyebrow">Resource group</p><h1>{group.name}</h1><p>{group.description || 'Organize workloads and shared services.'}</p></div>
      <Button aria-label="Refresh group details" disabled={details.status === 'loading'} onClick={() => void details.refresh()}>Refresh</Button></header>
    {details.error && <Notice tone="error">{details.error} Last fetched metadata is shown.</Notice>}
    <Tabs id={panelId} label={`${group.name} sections`} activeId={section} variant="section" onSelect={(value) => onSection(id, value as GroupSection)}
      tabs={[{ id: 'resources', label: 'Resources' }, { id: 'configuration', label: 'Configuration' }]} />
    <TabPanel id={panelId} tabId="resources" activeId={section}>
      <section className="panel"><div className="panel__header"><h2 className="panel__title">Group resources</h2><span className="panel__meta">{group.resourceCount} resource{group.resourceCount === 1 ? '' : 's'}</span></div>
        <p className="field__hint">{group.tags.length ? group.tags.join(', ') : 'No tags'}</p>
        {active && section === 'resources' && <GroupResources groupId={id} pageNumber={pageNumber} pageSize={pageSize} onPage={changePage} onPageSize={changePageSize} refreshVersion={refreshVersion} />}
      </section>
    </TabPanel>
    <TabPanel id={panelId} tabId="configuration" activeId={section}>
      {visitedConfiguration && (tenant.canManage ? <ResourceGroupForm key={formVersion} mode="edit" initialValue={group}
        submitStatus={manager.submitStatus} submitError={manager.submitError} onDirtyChange={setDirty} onCancelEdit={() => setDiscarding(true)}
        onSubmit={async (input) => { await manager.update(id, input); await details.refresh(); onChanged(); notifications.publish(`Metadata saved for ${group.name}.`) }} />
        : <EmptyState title="Tenant viewer access is read-only." />)}
    </TabPanel>
    {discarding && <ConfirmationDialog title="Discard group changes?" confirmLabel="Discard changes" onCancel={() => setDiscarding(false)}
      onConfirm={() => { setFormVersion((version) => version + 1); setDiscarding(false) }}>Your unsaved metadata changes will be lost.</ConfirmationDialog>}
  </div>
}

export function NewGroupView({ onDirtyChange, onCreated }:
  { onDirtyChange: (id: string, dirty: boolean) => void; onCreated: (group: ResourceGroup, open: boolean) => void }) {
  const { api } = useAppServices()
  const manager = useResourceGroupsManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: true, loadInventory: false })
  const [dirty, setDirty] = useState(false)
  const mounted = useRef(false)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  useUnsavedChanges(dirty, 'New resource group')
  useEffect(() => { onDirtyChange('create', dirty); return () => onDirtyChange('create', false) }, [dirty, onDirtyChange])
  return <ResourceGroupForm mode="create" submitStatus={manager.submitStatus} submitError={manager.submitError} onDirtyChange={setDirty}
    onSubmit={async (input) => { const group = await manager.create(input); if (group) onCreated(group, mounted.current) }} />
}
