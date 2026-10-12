import { useState } from 'react'
import { fetchSecrets } from '../../api'
import { useSecretQuery } from './queries'
import { useAppServices } from '../../shell/AppContext'
import { Button, DataTable, EmptyState, Notice, StatusBadge } from '../../ui/components'
import { ResourceWorkspace, type WorkspaceActions } from '../../ui/ResourceWorkspace'
import { NewSecretView } from './SecretCreate'
import { SecretResourceView } from './SecretResourceView'
import { secretSections, type SecretSection } from './workspaceState'

export function SecretsView() {
  return <ResourceWorkspace id="secrets" label="Secrets" newLabel="New Secret" sections={secretSections} defaultSection="overview" Catalog={SecretCatalog} Create={NewSecretView} Resource={SecretResourceView} />
}

function SecretCatalog({ actions, version }: { actions: WorkspaceActions<SecretSection>; version: number }) {
  const { api, tenant, navigation } = useAppServices()
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])
  const cursor = cursors.at(-1)
  const list = useSecretQuery(['secrets', 'inventory', cursor], (token, signal) => fetchSecrets(api.baseUrl, cursor, token, api.tenantId, signal), version)
  return <section className="panel"><header className="ui-page-header"><div><p className="ui-page-eyebrow">Security</p><h1>Secrets</h1><p>Encrypted values, resource bindings and deployment projections. Values cannot be read back.</p></div><div className="row-actions"><Button onClick={() => void list.refresh()} disabled={list.status === 'loading'}>Refresh Secrets</Button>{tenant.canManage && <Button variant="primary" onClick={() => actions.open('create')}>New Secret</Button>}</div></header>
    {list.error && <Notice tone="error">{list.error} Last fetched metadata is shown.</Notice>}
    {!list.resource?.items.length ? <EmptyState title={list.status === 'loading' ? 'Loading Secrets…' : list.status === 'error' ? 'Unable to load Secrets.' : 'No Secrets on this page.'} /> : <DataTable label="Secrets" items={list.resource.items} itemKey={(item) => item.id} columns={[
      { id: 'name', label: 'Secret', render: (item) => <a href={navigation.resourceHref({ resourceTypeId: 'pole/secret', resourceId: item.id })} onClick={(event) => { if (!event.ctrlKey && !event.metaKey && !event.shiftKey && event.button === 0) { event.preventDefault(); actions.loaded(item.id, item.name); actions.open(item.id) } }}>{item.name}</a> },
      { id: 'purpose', label: 'Purpose', render: (item) => item.purpose || '—' },
      { id: 'keys', label: 'Keys', render: (item) => item.keyNames.join(', ') },
      { id: 'version', label: 'Version', render: (item) => item.currentVersion },
      { id: 'owner', label: 'Management', render: (item) => <StatusBadge>{item.managedBy ? 'Provider managed' : 'Tenant managed'}</StatusBadge> },
    ]} />}
    <div className="row-actions" aria-label="Secret pages"><Button disabled={cursors.length === 1 || list.status === 'loading'} onClick={() => setCursors((items) => items.slice(0, -1))}>Previous page</Button><span>Page {cursors.length}</span><Button disabled={!list.resource?.nextCursor || list.status !== 'success'} onClick={() => setCursors((items) => [...items, list.resource!.nextCursor!])}>Next page</Button></div>
  </section>
}
