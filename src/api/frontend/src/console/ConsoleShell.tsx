import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { TopBandPlaceholder } from '../components/TopBandPlaceholder'
import type { useTenants } from '../hooks/useTenants'
import { ConsoleContext } from './ConsoleContext'
import { Button, EmptyState, Notice } from './components'
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
  const currentScope = useRef(scopeKey)
  useLayoutEffect(() => { currentScope.current = scopeKey }, [scopeKey])
  const publish = useCallback((message: string, tone: 'info' | 'error' = 'info') => {
    if (currentScope.current === scopeKey) setNotification({ message, tone, scopeKey })
  }, [scopeKey])
  const activeView = registry.views.find((view) => view.id === viewId)
  const goTo = (nextId: string) => {
    if (!registry.views.some((view) => view.id === nextId)) throw new Error(`Unknown console view: ${nextId}`)
    setViewId(nextId)
    setNotification(null)
    const url = new URL(window.location.href)
    if (nextId === registry.views[0]?.id) url.searchParams.delete('view')
    else url.searchParams.set('view', nextId)
    window.history.pushState({}, '', `${url.pathname}${url.search}${url.hash}`)
  }
  useEffect(() => {
    const onPopState = () => {
      setViewId(new URLSearchParams(window.location.search).get('view') ?? registry.views[0]?.id ?? '')
      setNotification(null)
    }
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [registry])
  useEffect(() => {
    document.documentElement.dataset.theme = theme
    localStorage.setItem('pole.console.theme', theme)
  }, [theme])

  const services: ConsoleServices = {
    auth,
    api: { ...api, tenantId: activeTenant?.id },
    tenant: tenantContext,
    navigation: { viewId, goTo },
    theme: { value: theme, set: setTheme },
    notifications: { publish },
  }
  const permitted = activeView && (activeView.access === 'public' ||
    (auth.authenticated && (activeView.access === 'authenticated' || activeTenant !== null)))
  const View = activeView?.component

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
          <div className="side-nav__footer"><p className="side-nav__footer-label">Session</p>
            <p className="side-nav__footer-value">{auth.authenticated ? auth.userName ?? 'Signed in' : 'Guest'}</p>
            <Button disabled={!auth.initialized} onClick={auth.authenticated ? auth.logout : auth.login}>{auth.authenticated ? 'Log out' : 'Log in'}</Button>
          </div>
        </div>
      </aside>
      <div className="app-shell__workspace">
        <header className="app-shell__top-band">
          <TopBandPlaceholder tenants={auth.authenticated ? tenants.items : []} activeTenantId={activeTenant?.id ?? ''}
            status={tenants.status} error={tenants.error} onSelectTenant={(id) => {
              setNotification(null)
              tenants.selectTenant(id)
            }} />
          <Button aria-label={`Switch to ${theme === 'light' ? 'dark' : 'light'} theme`}
            onClick={() => setTheme(theme === 'light' ? 'dark' : 'light')}>{theme === 'light' ? 'Dark theme' : 'Light theme'}</Button>
        </header>
        <main id="console-content" className="app-shell__content" tabIndex={-1}>
          {notification?.scopeKey === scopeKey && <div className="console-notification"><Notice tone={notification.tone}>{notification.message}</Notice>
            <Button aria-label="Dismiss notification" onClick={() => setNotification(null)}>Dismiss</Button></div>}
          <div className="console-view-bar"><span>{activeView?.label ?? 'Unavailable view'}</span>
            <div className="row-actions">{permitted && registry.commands.filter((command) => command.viewId === viewId &&
              (command.available?.({ auth, tenant: tenantContext }) ?? true)).map((command) =>
              <Button key={command.id} onClick={() => command.run(services)}>{command.label}</Button>)}</div>
          </div>
          {!auth.initialized ? <EmptyState title="Starting console…" /> : !activeView ?
            <EmptyState title="This view is unavailable." error><Button onClick={() => goTo(registry.views[0].id)}>Open overview</Button></EmptyState> :
            !permitted ? <EmptyState title={auth.authenticated ? 'Select an available tenant.' : 'Sign in to continue.'}>
              {tenants.status === 'error' && tenants.error}
              {!auth.authenticated && <Button onClick={auth.login}>Log in</Button>}
            </EmptyState> : View && <View key={`${scopeKey}:${viewId}`} />}
        </main>
      </div>
    </div>
  </ConsoleContext.Provider>
}
