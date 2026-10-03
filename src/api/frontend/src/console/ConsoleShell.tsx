import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { TopBandPlaceholder } from '../components/TopBandPlaceholder'
import type { useTenants } from '../hooks/useTenants'
import { ConsoleContext } from './ConsoleContext'
import { Button, ConfirmationDialog, EmptyState, Notice } from './components'
import type { ConsoleRegistry, ConsoleServices, Theme } from './contracts'

interface ConsoleShellProps {
  registry: ConsoleRegistry
  auth: ConsoleServices['auth']
  api: ConsoleServices['api']
  tenants: ReturnType<typeof useTenants>
  initialViewId: string
}

function initialTheme(): Theme {
  const stored = localStorage.getItem('pole.console.theme')
  if (stored === 'light' || stored === 'dark') return stored
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

export function ConsoleShell({ registry, auth, api, tenants, initialViewId }: ConsoleShellProps) {
  const [viewId, setViewId] = useState(initialViewId)
  const [theme, setTheme] = useState(initialTheme)
  const [notification, setNotification] = useState<{ message: string; tone: 'info' | 'error'; scopeKey: string } | null>(null)
  const activeTenant = auth.authenticated ? tenants.activeTenant : null
  const tenantContext = { active: activeTenant, canManage: ['Owner', 'Admin', 'Member'].includes(activeTenant?.role ?? '') }
  const scopeKey = `${auth.authenticated ? auth.userId : 'guest'}:${activeTenant?.id ?? 'no-tenant'}`
  const [unsavedChanges, setUnsavedChanges] = useState<Map<symbol, { label: string; scopeKey: string }>>(new Map())
  const [pendingNavigation, setPendingNavigation] = useState<{ run: () => void; scopeKey: string } | null>(null)
  const [navigationScope, setNavigationScope] = useState(scopeKey)
  if (navigationScope !== scopeKey) {
    setNavigationScope(scopeKey)
    setPendingNavigation(null)
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
  useLayoutEffect(() => { currentScope.current = scopeKey }, [scopeKey])
  const publish = useCallback((message: string, tone: 'info' | 'error' = 'info') => {
    if (currentScope.current === scopeKey) setNotification({ message, tone, scopeKey })
  }, [scopeKey])
  const activeView = registry.views.find((view) => view.id === viewId)
  const goTo = (nextId: string) => {
    if (!registry.views.some((view) => view.id === nextId)) throw new Error(`Unknown console view: ${nextId}`)
    if (nextId === viewId) return
    runGuarded(() => {
      setViewId(nextId)
      setNotification(null)
      const url = new URL(window.location.href)
      if (nextId === registry.views[0]?.id) url.searchParams.delete('view')
      else url.searchParams.set('view', nextId)
      window.history.pushState({}, '', `${url.pathname}${url.search}${url.hash}`)
    })
  }
  useEffect(() => {
    const currentUrl = window.location.href
    const onPopState = () => {
      const nextViewId = new URLSearchParams(window.location.search).get('view') ?? registry.views[0]?.id ?? ''
      if (nextViewId === viewId) return
      if (dirtyLabels.length && !window.confirm('Discard unsaved changes and leave this view?')) {
        window.history.pushState({}, '', currentUrl)
        return
      }
      setViewId(nextViewId)
      setNotification(null)
    }
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [registry, viewId, dirtyLabels.length])
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
    localStorage.setItem('pole.console.theme', theme)
  }, [theme])

  const services: ConsoleServices = {
    auth,
    api: { ...api, tenantId: activeTenant?.id },
    tenant: tenantContext,
    navigation: { viewId, goTo, registerUnsavedChanges },
    theme: { value: theme, set: setTheme },
    notifications: { publish },
  }
  const permitted = activeView && (activeView.access === 'public' ||
    (auth.authenticated && (activeView.access === 'authenticated' || activeTenant !== null)))
  const View = activeView?.component
  const commands = permitted ? registry.commands.filter((command) => command.viewId === viewId &&
    (command.available?.({ auth, tenant: tenantContext }) ?? true)) : []

  return <ConsoleContext.Provider value={services}>
    <div className="app-shell">
      <a className="skip-link" href="#console-content">Skip to content</a>
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
        </div>
      </aside>
      <div className="app-shell__workspace">
        <header className="app-shell__top-band">
          <TopBandPlaceholder tenants={auth.authenticated ? tenants.items : []} activeTenantId={activeTenant?.id ?? ''}
            status={tenants.status} error={tenants.error} onSelectTenant={(id) => runGuarded(() => {
              setNotification(null)
              tenants.selectTenant(id)
            })} />
          <div className="console-header-actions">
            <Button aria-label={`Switch to ${theme === 'light' ? 'dark' : 'light'} theme`}
              onClick={() => setTheme(theme === 'light' ? 'dark' : 'light')}>{theme === 'light' ? 'Dark theme' : 'Light theme'}</Button>
            <Button disabled={!auth.initialized} onClick={() => runGuarded(auth.authenticated ? auth.logout : auth.login)}>{auth.authenticated ? 'Log out' : 'Log in'}</Button>
          </div>
        </header>
        <main id="console-content" className="app-shell__content" tabIndex={-1}>
          {notification?.scopeKey === scopeKey && <div className="console-notification"><Notice tone={notification.tone}>{notification.message}</Notice>
            <Button aria-label="Dismiss notification" onClick={() => setNotification(null)}>Dismiss</Button></div>}
          {commands.length > 0 && <div className="console-view-bar"><span>{activeView?.label ?? 'Unavailable view'}</span>
            <div className="row-actions">{commands.map((command) =>
              <Button key={command.id} onClick={() => command.run(services)}>{command.label}</Button>)}</div>
          </div>}
          {!auth.initialized ? <EmptyState title="Starting console…" /> : !activeView ?
            <EmptyState title="This view is unavailable." error><Button onClick={() => goTo(registry.views[0].id)}>Open overview</Button></EmptyState> :
            !permitted ? <EmptyState title={auth.authenticated ? 'Select an available tenant.' : 'Sign in to continue.'}>
              {tenants.status === 'error' && tenants.error}
              {!auth.authenticated && <Button onClick={auth.login}>Log in</Button>}
            </EmptyState> : View && <View key={`${scopeKey}:${viewId}`} />}
        </main>
      </div>
      {pendingNavigation?.scopeKey === scopeKey && <ConfirmationDialog title="Discard unsaved changes?"
        confirmLabel="Discard and continue" onCancel={() => setPendingNavigation(null)} onConfirm={() => {
          pendingNavigation.run()
          setPendingNavigation(null)
        }}><p>Your changes in {dirtyLabels.join(', ')} will be lost.</p></ConfirmationDialog>}
    </div>
  </ConsoleContext.Provider>
}
