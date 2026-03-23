import type { AppView } from '../types'

interface SideNavProps {
  authenticated: boolean
  userName?: string
  currentView: AppView
  onSelectView: (view: AppView) => void
}

const navigationItems = [
  { label: 'Overview', view: 'overview' as const, disabled: false },
  { label: 'Resource groups', view: 'resource-groups' as const, disabled: false },
  { label: 'Web services', view: 'web-services' as const, disabled: false },
  { label: 'Databases', view: null, disabled: true },
]

export function SideNav({
  authenticated,
  userName,
  currentView,
  onSelectView,
}: SideNavProps) {
  return (
    <div className="side-nav">
      <div className="side-nav__brand">
        <div className="side-nav__brand-mark" aria-hidden="true">
          P
        </div>
        <div>
          <p className="side-nav__eyebrow">Pole control plane</p>
          <h1 className="side-nav__title">Home lab cloud</h1>
        </div>
      </div>

      <nav className="side-nav__menu" aria-label="Primary">
        {navigationItems.map((item) => (
          <button
            key={item.label}
            className={`side-nav__item${currentView === item.view ? ' is-active' : ''}`}
            disabled={item.disabled}
            onClick={() => {
              if (item.view) {
                onSelectView(item.view)
              }
            }}
            type="button"
          >
            <span className="side-nav__item-indicator" aria-hidden="true" />
            {item.label}
          </button>
        ))}
      </nav>

      <div className="side-nav__footer">
        <p className="side-nav__footer-label">Session</p>
        <p className="side-nav__footer-value">
          {authenticated ? userName ?? 'Authenticated operator' : 'Guest mode'}
        </p>
        <p className="side-nav__footer-meta">
          {authenticated
            ? 'Signed in through Keycloak.'
            : 'Browsing without an active session.'}
        </p>
      </div>
    </div>
  )
}
