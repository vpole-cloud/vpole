import { SummaryPanel } from './SummaryPanel'
import { ResourceGroupList } from './ResourceGroupList'
import { WebServiceList } from './WebServiceList'
import type { AuthViewState, LoadStatus, OverviewData } from '../types'

interface OverviewPageProps {
  authState: AuthViewState
  onLogin: () => void
  onLogout: () => void
  onValidateIdentity: () => void
  overview: {
    status: LoadStatus
    error: string
    data: OverviewData
    identityStatus: LoadStatus
    identityMessage: string
  }
}

const getPlatformHealth = (
  status: LoadStatus,
  authenticated: boolean,
): { value: string; helper: string; tone: 'default' | 'positive' } => {
  if (status === 'error') {
    return {
      value: 'Attention needed',
      helper: 'Overview data could not be loaded from the API.',
      tone: 'default',
    }
  }

  if (authenticated) {
    return {
      value: 'Connected',
      helper: 'Authenticated operator session is available.',
      tone: 'positive',
    }
  }

  return {
    value: 'Preview mode',
    helper: 'The shell is available before sign-in.',
    tone: 'default',
  }
}

export function OverviewPage({
  authState,
  onLogin,
  onLogout,
  onValidateIdentity,
  overview,
}: OverviewPageProps) {
  const health = getPlatformHealth(overview.status, authState.authenticated)
  const totalResources = overview.data.resourceGroups.reduce(
    (total, group) => total + group.resourceCount,
    0,
  )

  return (
    <div className="overview-page">
      <section className="hero-panel">
        <div className="hero-panel__main">
          <p className="hero-panel__eyebrow">Overview</p>
          <h1 className="hero-panel__title">Control plane</h1>
          <p className="hero-panel__description">
            A desktop-first workspace for operating your self-hosted platform
            with a calmer portal shell and quick access to the resources that
            matter first.
          </p>
        </div>

        <aside className="hero-panel__utility">
          <div className="session-card">
            <div>
              <p className="session-card__label">Session</p>
              <p className="session-card__value">
                {!authState.initialized
                  ? 'Initializing access'
                  : authState.authenticated
                    ? authState.userName ?? 'Authenticated operator'
                    : 'Guest mode'}
              </p>
            </div>

            <div className="session-card__actions">
              <button
                className="button button--primary"
                onClick={onLogin}
                disabled={!authState.initialized || authState.authenticated}
                type="button"
              >
                Log in
              </button>
              <button
                className="button"
                onClick={onLogout}
                disabled={!authState.authenticated}
                type="button"
              >
                Log out
              </button>
              <button
                className="button"
                onClick={onValidateIdentity}
                disabled={!authState.authenticated || overview.identityStatus === 'loading'}
                type="button"
              >
                Validate identity
              </button>
            </div>

            <p className="session-card__note">
              {overview.identityStatus === 'loading'
                ? 'Checking protected endpoint...'
                : overview.identityMessage ||
                  'Use the protected check to confirm the active Keycloak session.'}
            </p>
          </div>
        </aside>
      </section>

      <section className="summary-grid" aria-label="Summary">
        <SummaryPanel
          label="Platform state"
          value={health.value}
          helper={health.helper}
          tone={health.tone}
        />
        <SummaryPanel
          label="Resource groups"
          value={String(overview.data.resourceGroups.length)}
          helper="Organize environments, apps, and platform services."
        />
        <SummaryPanel
          label="Managed resources"
          value={String(totalResources)}
          helper="Resources discovered across the visible groups."
        />
        <SummaryPanel
          label="Web services"
          value={String(overview.data.webServices.length)}
          helper="Current application deployments surfaced by the API."
        />
      </section>

      <section className="overview-layout">
        <ResourceGroupList
          items={overview.data.resourceGroups}
          status={overview.status}
          error={overview.error}
        />
        <WebServiceList
          items={overview.data.webServices}
          groups={overview.data.resourceGroups}
          status={overview.status}
          error={overview.error}
        />
      </section>
    </div>
  )
}
