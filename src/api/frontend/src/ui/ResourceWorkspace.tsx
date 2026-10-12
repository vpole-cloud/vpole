import { useWorkspaceController } from '../shell/useWorkspaceController'
import type { ComponentType } from 'react'
import { useAppServices } from '../shell/AppContext'
import { catalogId, createId } from '../shell/workspaceState'
import { Button, ConfirmationDialog, EmptyState } from './components'
import { Tabs, TabPanel } from './Tabs'

export interface WorkspaceActions<S extends string> {
  open: (id: string, section?: S) => void
  created: (id: string, name: string) => void
  loaded: (id: string, name: string) => void
  dirty: (id: string, dirty: boolean) => void
  closed: (id: string) => void
}

// Only workspace navigation mechanics are shared. Providers own their data, forms and actions.
export function ResourceWorkspace<S extends string>({ id, label, newLabel, sections, defaultSection, Catalog, Create, Resource }:
  { id: string; label: string; newLabel: string; sections: readonly S[]; defaultSection: S;
    Catalog: ComponentType<{ actions: WorkspaceActions<S>; active: boolean }>;
    Create: ComponentType<{ actions: WorkspaceActions<S>; active: boolean }>;
    Resource: ComponentType<{ id: string; section: S; active: boolean; onSection: (section: S) => void; actions: WorkspaceActions<S> }> }) {
  const { tenant } = useAppServices()
  const { route, workspace, creating, setCreating, closingId: closing, setClosingId: setClosing, names, dirtyTabs,
    isMounted, navigateWorkspace: navigate, loaded, dirty, open, section: selectSection, close: closed } = useWorkspaceController({ id, sections, defaultSection })
  const workspaceId = `${id}-workspace`
  const actions: WorkspaceActions<S> = { open, loaded, dirty, closed,
    created: (resourceId, name) => { if (isMounted()) { setCreating(false); loaded(resourceId, name); open(resourceId) } } }
  const activeId = route.valid ? workspace.activeId : 'invalid'
  const name = (id: string) => id === createId ? newLabel : names[id] ?? `Resource ${id.slice(0, 8)}`
  return <div id={workspaceId}>
    {!route.valid && <EmptyState title="This resource link is invalid." error><Button onClick={() => navigate({ ...workspace, activeId: catalogId }, true)}>Open catalog</Button></EmptyState>}
    <Tabs id={workspaceId} label={`${label} workspace`} activeId={activeId} onSelect={(activeId) => navigate({ ...workspace, activeId })}
      tabs={[{ id: catalogId, label: `All ${label}` }, ...(creating ? [{ id: createId, label: newLabel, dirty: dirtyTabs[createId], onClose: () => dirtyTabs[createId] ? setClosing(createId) : closed(createId) }] : []),
        ...workspace.tabs.map((tab) => ({ id: tab.id, label: name(tab.id), dirty: dirtyTabs[tab.id], onClose: () => dirtyTabs[tab.id] ? setClosing(tab.id) : closed(tab.id) }))]} />
    <TabPanel id={workspaceId} tabId={catalogId} activeId={activeId}>
      <Catalog active={activeId === catalogId} actions={{ ...actions, open: (id, section) => { if (id === createId) setCreating(true); open(id, section) } }} />
    </TabPanel>
    {creating && <TabPanel id={workspaceId} tabId={createId} activeId={activeId}>{tenant.canManage ? <Create actions={actions} active={activeId === createId} /> : <EmptyState title="Tenant viewer access is read-only." />}</TabPanel>}
    {workspace.tabs.map((tab) => <TabPanel key={tab.id} id={workspaceId} tabId={tab.id} activeId={activeId}>
      <Resource id={tab.id} section={tab.section} active={activeId === tab.id}
        onSection={(section) => selectSection(tab.id, section)} actions={actions} />
    </TabPanel>)}
    {closing && <ConfirmationDialog title={`Discard changes to ${name(closing)}?`} confirmLabel="Discard and close" onCancel={() => setClosing(null)} onConfirm={() => closed(closing)}>
      Unsaved changes will be lost.</ConfirmationDialog>}
  </div>
}
