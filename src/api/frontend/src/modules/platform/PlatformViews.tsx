// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { ResourceOperations } from '../../ui/ResourceOperations'
import { useEffect, useState } from 'react'
import { OverviewPage } from '../../components/OverviewPage'
import { IntegrationsPage } from '../../components/IntegrationsPage'
import { StorageSettingsPage } from '../../components/StorageSettingsPage'
import { useOverviewData } from './useOverviewData'
import { useGitIntegrationsManager } from './useGitIntegrationsManager'
import { useStorageSettingsManager } from './useStorageSettingsManager'
import { readIntegrationNavigation } from './integrationsState'
import { useAppServices } from '../../shell/AppContext'

function useTenantApiOptions() {
  const { api, tenant } = useAppServices()
  return {
    apiBaseUrl: api.baseUrl,
    token: api.token,
    tenantId: api.tenantId,
    enabled: Boolean(tenant.active),
  }
}

export function OverviewView() {
  const { auth, tenant, resourceTypes, navigation } = useAppServices()
  const overview = useOverviewData(useTenantApiOptions())
  return (
    <>
      <OverviewPage
        authState={auth}
        onValidateIdentity={() => {
          void overview.runIdentityCheck()
        }}
        overview={overview}
      />
      {tenant.active && <ResourceOperations />}
      {tenant.active && (
        <section className="panel" aria-label="Resource catalogs">
          <h2 className="panel__title">Resource catalogs</h2>
          <p className="field__hint">Open a resource catalog in the active tenant.</p>
          <div className="row-actions">
            {resourceTypes.map((type) => (
              <a
                key={type.id}
                className="button"
                href={navigation.resourceHref({ resourceTypeId: type.id, resourceId: 'catalog' })}
              >
                {type.label}
              </a>
            ))}
          </div>
        </section>
      )}
    </>
  )
}

export function IntegrationsView() {
  const {
    tenant,
    navigation: { search, setSearch },
  } = useAppServices()
  const [notice, setNotice] = useState(
    () => readIntegrationNavigation(window.location.search).githubNotice,
  )
  const manager = useGitIntegrationsManager({
    ...useTenantApiOptions(),
    canManage: tenant.canManage,
    preferredConnectionId: notice?.status === 'connected' ? notice.connectionId : undefined,
  })
  useEffect(() => {
    const params = new URLSearchParams(search)
    if (!['github', 'connectionId', 'reason'].some((key) => params.has(key))) return
    for (const key of ['github', 'connectionId', 'reason']) params.delete(key)
    setSearch(params, { replace: true })
  }, [search, setSearch])
  return (
    <IntegrationsPage
      manager={manager}
      notice={notice}
      onDismissNotice={() => setNotice(null)}
      canManage={tenant.canManage}
    />
  )
}

export function StorageView() {
  const { api, auth } = useAppServices()
  const storage = useStorageSettingsManager({
    apiBaseUrl: api.baseUrl,
    token: api.token,
    enabled: auth.authenticated && auth.isPlatformAdmin,
  })
  return (
    <StorageSettingsPage
      key={auth.isPlatformAdmin ? 'admin' : 'viewer'}
      {...storage}
      onRefresh={storage.refresh}
      onCreate={storage.create}
      onUpdate={storage.update}
      onSelectTarget={storage.selectTarget}
      canManage={auth.isPlatformAdmin}
    />
  )
}
