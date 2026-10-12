import { SessionQueries } from '../data/SessionQueries'
import { useQuerySession } from '../data/querySession'
import { Suspense, useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { TopBandPlaceholder } from '../components/TopBandPlaceholder'
import type { useTenants } from '../hooks/useTenants'
import { AppContext } from './AppContext'
import { Button, ConfirmationDialog, EmptyState, Notice } from '../ui/components'
import type { AppRegistry, AppServices, ResourceIdentity, Theme } from './contracts'
import { SearchPalette } from './SearchPalette'
import { ModuleViewBoundary } from './ModuleViewBoundary'

interface AppShellProps {
  registry: AppRegistry
  auth: AppServices['auth']
  api: AppServices['api']
  tenants: ReturnType<typeof useTenants>
  initialViewId: string
}

function initialTheme(): Theme {
  let stored: string | null = null
  try { stored = localStorage.getItem('pole.console.theme') } catch { /* Use the system preference when storage is disabled. */ }
  if (stored === 'light' || stored === 'dark') return stored
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

export function AppShell(props: AppShellProps) {
  const identity = `${props.api.baseUrl}:${props.auth.authenticated}:${props.auth.userId}`
  return <SessionQueries identity={identity}><AppShellContent {...props} /></SessionQueries>
}

function AppShellContent({ registry, auth, api, tenants, initialViewId }: AppShellProps) {
  const session = useQuerySession()
  const readView = (params: URLSearchParams, fallback: string) => {
    const type = params.get('resourceType')
    return type ? registry.resourceTypes.find((entry) => entry.id === type)?.viewId ?? 'unavailable-resource'
      : params.get('view') ?? fallback
  }
  const [location, setLocation] = useState(() => ({
    viewId: readView(new URLSearchParams(window.location.search), initialViewId),
    search: window.location.search,
  }))
  const { viewId } = location
  const requestedTenantId = new URLSearchParams(location.search).get('tenant')
  const [theme, setTheme] = useState(initialTheme)
  const [searchOpen, setSearchOpen] = useState(false)
  const [notification, setNotification] = useState<{ message: string; tone: 'info' | 'error'; scopeKey: string } | null>(null)
  const activeTenant = auth.authenticated ? requestedTenantId !== null
    ? tenants.items.find((tenant) => tenant.id === requestedTenantId) ?? null : tenants.activeTenant : null
  useEffect(() => {
    if (activeTenant && requestedTenantId && tenants.activeTenantId !== activeTenant.id) tenants.selectTenant(activeTenant.id)
  }, [activeTenant, requestedTenantId, tenants])
  const tenantContext = { active: activeTenant, canManage: ['Owner', 'Admin', 'Member'].includes(activeTenant?.role ?? '') }
  const scopeKey = `${auth.authenticated ? auth.userId : 'guest'}:${activeTenant?.id ?? 'no-tenant'}`
  useLayoutEffect(() => {
    return () => { void session.client.cancelQueries({ queryKey: ['pole', session.identity, session.id, api.baseUrl, activeTenant?.id] }) }
  }, [session, api.baseUrl, activeTenant?.id])
  const [unsavedChanges, setUnsavedChanges] = useState<Map<symbol, { label: string; scopeKey: string }>>(new Map())
  const [pendingNavigation, setPendingNavigation] = useState<{ run: () => void; scopeKey: string } | null>(null)
  const [navigationScope, setNavigationScope] = useState(scopeKey)
  if (navigationScope !== scopeKey) {
    setNavigationScope(scopeKey)
    setPendingNavigation(null)
    setSearchOpen(false)
  }
  const dirtyLabels = [...unsavedChanges.values()].filter((entry) => entry.scopeKey === scopeKey).map((entry) => entry.label)
  const registerUnsavedChanges = useCallback((label: string) => {
    const registration = Symbol(label)
    setUnsavedChanges((current) => new Map(current).set(registration, { label, scopeKey }))
    return () => setUnsavedChanges((current) => {
      const next = new Map(current)
      next.delete(registration)
      return next
    })
  }, [scopeKey])
  const runGuarded = (run: () => void) => {
    if (dirtyLabels.length) setPendingNavigation({ run, scopeKey })
    else run()
  }
  const currentScope = useRef(scopeKey)
  const currentView = useRef(viewId)
  useLayoutEffect(() => { currentScope.current = scopeKey; currentView.current = viewId }, [scopeKey, viewId])
  const publish = useCallback((message: string, tone: 'info' | 'error' = 'info') => {
    if (currentScope.current === scopeKey) setNotification({ message, tone, scopeKey })
  }, [scopeKey])
  const activeView = registry.views.find((view) => view.id === viewId)
  const historyEntry = useRef({ url: window.location.href,
    index: typeof window.history.state?.poleNavigationIndex === 'number' ? window.history.state.poleNavigationIndex : 0 })
  const restoringHistory = useRef(false)
  useLayoutEffect(() => {
    window.history.replaceState({ ...window.history.state, poleNavigationIndex: historyEntry.current.index }, '', window.location.href)
  }, [])
  const writeLocation = useCallback((params: URLSearchParams, nextViewId: string, replace = false, fragment?: string) => {
    const url = new URL(window.location.href)
    url.search = params.toString()
    if (fragment !== undefined) url.hash = fragment
    if (url.href === historyEntry.current.url) return
    const index = historyEntry.current.index + (replace ? 0 : 1)
    window.history[replace ? 'replaceState' : 'pushState']({ ...window.history.state, poleNavigationIndex: index }, '', url)
    historyEntry.current = { url: url.href, index }
    setLocation({ viewId: nextViewId, search: url.search })
  }, [])
  const setSearch = useCallback((params: URLSearchParams, options?: { replace?: boolean }) => {
    // A completion from an unmounted tenant/view cannot change the current URL.
    if (currentScope.current !== scopeKey || currentView.current !== viewId) return
    const next = new URLSearchParams(params)
    next.set('view', viewId)
    if (activeTenant) next.set('tenant', activeTenant.id)
    writeLocation(next, viewId, options?.replace)
  }, [activeTenant, scopeKey, viewId, writeLocation])
  const goTo = (nextId: string) => {
    if (!registry.views.some((view) => view.id === nextId)) throw new Error(`Unknown app view: ${nextId}`)
    if (nextId === viewId) return
    runGuarded(() => {
      setNotification(null)
      const params = new URLSearchParams()
      if (nextId !== registry.views[0]?.id) params.set('view', nextId)
      if (activeTenant) params.set('tenant', activeTenant.id)
      writeLocation(params, nextId)
    })
  }
  const resourceParams = (resource: ResourceIdentity) => {
    const registration = registry.resourceTypes.find((entry) => entry.id === resource.resourceTypeId)
    const params = new URLSearchParams({ view: registration?.viewId ?? 'unavailable-resource',
      resourceType: resource.resourceTypeId, resource: resource.resourceId })
    if (activeTenant) params.set('tenant', activeTenant.id)
    if (resource.section) params.set('section', resource.section)
    return params
  }
  const resourceHref = (resource: ResourceIdentity) => {
    const url = new URL(window.location.href)
    url.search = resourceParams(resource).toString()
    url.hash = resource.fragment ?? ''
    return `${url.pathname}${url.search}${url.hash}`
  }
  const openResource = (resource: ResourceIdentity) => {
    const params = resourceParams(resource)
    const nextId = params.get('view')!
    const run = () => { setNotification(null); writeLocation(params, nextId, false, resource.fragment ?? '') }
    if (nextId === viewId) run()
    else runGuarded(run)
  }
  useEffect(() => {
    const params = new URLSearchParams(location.search)
    if (params.has('resourceType') && params.get('view') !== viewId) {
      params.set('view', viewId)
      writeLocation(params, viewId, true)
    }
  }, [location.search, viewId, writeLocation])
  useEffect(() => {
    const onPopState = () => {
      if (restoringHistory.current) { restoringHistory.current = false; return }
      const params = new URLSearchParams(window.location.search)
      const type = params.get('resourceType')
      const nextViewId = type ? registry.resourceTypes.find((entry) => entry.id === type)?.viewId ?? 'unavailable-resource'
        : params.get('view') ?? registry.views[0]?.id ?? ''
      const nextTenantId = params.get('tenant') ?? tenants.activeTenant?.id
      const index = window.history.state?.poleNavigationIndex
      const abandonsView = nextViewId !== viewId || nextTenantId !== activeTenant?.id
      if (abandonsView && dirtyLabels.length && !window.confirm('Discard unsaved changes and leave this view?')) {
        if (typeof index === 'number' && index !== historyEntry.current.index) {
          restoringHistory.current = true
          window.history.go(historyEntry.current.index - index)
        } else window.history.replaceState({ ...window.history.state, poleNavigationIndex: historyEntry.current.index }, '', historyEntry.current.url)
        return
      }
      historyEntry.current = { url: window.location.href, index: typeof index === 'number' ? index : 0 }
      setLocation({ viewId: nextViewId, search: window.location.search })
      setNotification(null)
    }
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [registry, viewId, activeTenant?.id, tenants.activeTenant?.id, dirtyLabels.length])
  useEffect(() => {
    if (!dirtyLabels.length) return
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', onBeforeUnload)
    return () => window.removeEventListener('beforeunload', onBeforeUnload)
  }, [dirtyLabels.length])
  useEffect(() => {
    document.documentElement.dataset.theme = theme
    try { localStorage.setItem('pole.console.theme', theme) } catch { /* The theme remains usable in memory. */ }
  }, [theme])
  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      if (auth.authenticated && activeTenant && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        setSearchOpen((current) => !current)
      }
    }
    window.addEventListener('keydown', shortcut)
    return () => window.removeEventListener('keydown', shortcut)
  }, [auth.authenticated, activeTenant])

  const services: AppServices = {
    auth,
    api: { ...api, tenantId: activeTenant?.id },
    tenant: tenantContext,
    navigation: { viewId, search: location.search, setSearch, goTo, openResource, resourceHref, registerUnsavedChanges },
    theme: { value: theme, set: setTheme },
    notifications: { publish },
    resourceTypes: registry.resourceTypes,
  }
  const permitted = activeView && (activeView.access === 'public' ||
    (auth.authenticated && (activeView.access === 'authenticated' || activeTenant !== null)))
  const View = activeView?.component
  const commands = permitted ? registry.commands.filter((command) => command.surface !== 'palette' && command.viewId === viewId &&
    (command.available?.({ auth, tenant: tenantContext, resourceTypes: registry.resourceTypes,
      location: { viewId, search: location.search } }) ?? true)) : []

  return <AppContext.Provider value={services}>
    <div className="app-shell">
      <a className="skip-link" href="#app-content">Skip to content</a>
      <aside className="app-shell__side-nav">
        <div className="side-nav">
          <div className="side-nav__brand"><span className="side-nav__brand-mark" aria-hidden="true">P</span>
            <div><p className="side-nav__eyebrow">Control plane</p><p className="side-nav__title">Pole</p></div>
          </div>
          <nav className="side-nav__menu" aria-label="Primary">
            {registry.views.map((view) => <button key={view.id} type="button"
              className={`side-nav__item${view.id === viewId ? ' is-active' : ''}`}
              aria-current={view.id === viewId ? 'page' : undefined}
              disabled={view.access !== 'public' && !auth.authenticated}
              onClick={() => goTo(view.id)}>{view.label}</button>)}
          </nav>
          {auth.authenticated && activeTenant && registry.navigationPanels.map(({ id, component: Panel }) =>
            <Panel key={`${scopeKey}:${id}`} />)}
        </div>
      </aside>
      <div className="app-shell__workspace">
        <header className="app-shell__top-band">
          <TopBandPlaceholder tenants={auth.authenticated ? tenants.items : []} activeTenantId={activeTenant?.id ?? ''}
            status={tenants.status} error={tenants.error} onSelectTenant={(id) => {
              if (id === activeTenant?.id) return
              runGuarded(() => {
                setNotification(null)
                tenants.selectTenant(id)
                const params = new URLSearchParams({ view: viewId, tenant: id })
                writeLocation(params, viewId)
              })
            }} />
          <div className="ui-header-actions">
            {auth.authenticated && activeTenant && <Button onClick={() => setSearchOpen(true)} aria-label="Search and commands" aria-keyshortcuts="Control+k Meta+k">Search <kbd>⌘/Ctrl K</kbd></Button>}
            <Button aria-label={`Switch to ${theme === 'light' ? 'dark' : 'light'} theme`}
              onClick={() => setTheme(theme === 'light' ? 'dark' : 'light')}>{theme === 'light' ? 'Dark theme' : 'Light theme'}</Button>
            <Button disabled={!auth.initialized} onClick={() => runGuarded(auth.authenticated ? auth.logout : auth.login)}>{auth.authenticated ? 'Log out' : 'Log in'}</Button>
          </div>
        </header>
        <main id="app-content" className="app-shell__content" tabIndex={-1}>
          {notification?.scopeKey === scopeKey && <div className="ui-notification"><Notice tone={notification.tone}>{notification.message}</Notice>
            <Button aria-label="Dismiss notification" onClick={() => setNotification(null)}>Dismiss</Button></div>}
          {commands.length > 0 && <div className="ui-view-bar"><span>{activeView?.label ?? 'Unavailable view'}</span>
            <div className="row-actions">{commands.map((command) =>
              <Button key={command.id} onClick={() => command.run(services)}>{command.label}</Button>)}</div>
          </div>}
          {!auth.initialized ? <EmptyState title="Starting workspace…" /> : !activeView ?
            <EmptyState title={viewId === 'unavailable-resource' ? 'This resource provider is unavailable.' : 'This view is unavailable.'} error>
              {viewId === 'unavailable-resource' && <p>The UI for {new URLSearchParams(location.search).get('resourceType')} is not registered. This link does not grant resource access.</p>}
              <Button onClick={() => goTo(registry.views[0].id)}>Open overview</Button></EmptyState> :
            !permitted ? <EmptyState title={auth.authenticated ? requestedTenantId !== null && tenants.status === 'success'
              ? 'This tenant is unavailable to your account. Select an available tenant.' : 'Select an available tenant.' : 'Sign in to continue.'}>
              {tenants.status === 'error' && tenants.error}
              {!auth.authenticated && <Button onClick={auth.login}>Log in</Button>}
            </EmptyState> : View && <ModuleViewBoundary key={`${scopeKey}:${viewId}`}>
              <Suspense fallback={<div role="status"><EmptyState title="Loading view…" /></div>}><View /></Suspense>
            </ModuleViewBoundary>}
        </main>
      </div>
      {pendingNavigation?.scopeKey === scopeKey && <ConfirmationDialog title="Discard unsaved changes?"
        confirmLabel="Discard and continue" onCancel={() => setPendingNavigation(null)} onConfirm={() => {
          pendingNavigation.run()
          setPendingNavigation(null)
        }}><p>Your changes in {dirtyLabels.join(', ')} will be lost.</p></ConfirmationDialog>}
      {searchOpen && auth.authenticated && activeTenant && <SearchPalette key={scopeKey} registry={registry} onClose={() => setSearchOpen(false)} />}
    </div>
  </AppContext.Provider>
}
