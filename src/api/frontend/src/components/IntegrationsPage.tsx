import { useState } from 'react'
import type { GitHubConnectionNotice } from '../modules/platform/integrationsState'
import type { GitIntegrationsManager } from '../modules/platform/useGitIntegrationsManager'

interface IntegrationsPageProps {
  manager: GitIntegrationsManager
  notice: GitHubConnectionNotice | null
  onDismissNotice: () => void
  canManage: boolean
}

const callbackErrors: Record<string, string> = {
  'not-configured': 'GitHub App integration is not configured.',
  'invalid-installation-callback': 'GitHub returned an invalid installation callback.',
  'expired-or-invalid-state': 'The GitHub setup session expired or was already used.',
  'contents-permission-required': 'The installation must grant read access to repository contents.',
  'github-installation-unavailable': 'GitHub could not verify the installation.',
  'installation-access-denied': 'Your GitHub user cannot access this installation.',
  'connection-name-conflict': 'This connection name is already used in the tenant.',
  'connection-name-mismatch':
    'This installation is already connected with a different immutable name.',
  'installation-already-linked': 'This GitHub installation belongs to another Pole tenant.',
  'oauth-verification-failed': 'GitHub user verification failed.',
}

const shortSha = (value: string) => value.slice(0, 10)

const formatDate = (value: string) =>
  new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value))

export function IntegrationsPage({
  manager,
  notice,
  onDismissNotice,
  canManage,
}: IntegrationsPageProps) {
  const [previewMode, setPreviewMode] = useState<'dockerfile' | 'commands'>('dockerfile')
  const selectedConnection = manager.connections.find(
    (connection) => connection.id === manager.selection.connectionId,
  )
  const selectedRepository = manager.repositories.find(
    (repository) => repository.id === manager.selection.repositoryId,
  )
  const selectedPlan = manager.selection.analysis?.candidates.find(
    (candidate) => candidate.candidateKey === manager.selection.candidateKey,
  )
  const isLinked = Boolean(
    manager.sourceRepositories.some(
      (source) =>
        source.connectionId === manager.selection.connectionId &&
        source.externalRepositoryId === manager.selection.repositoryId &&
        source.branch === manager.selection.branch,
    ),
  )

  const disconnect = async (connectionId: string) => {
    if (
      window.confirm(
        'Remove this connection from Pole? The GitHub App installation will remain installed on GitHub.',
      )
    ) {
      await manager.disconnect(connectionId)
    }
  }

  const unlink = async (sourceRepositoryId: string, fullName: string) => {
    if (window.confirm(`Unlink ${fullName} from Pole?`)) {
      await manager.unlink(sourceRepositoryId)
    }
  }

  return (
    <div className="integrations-page">
      <header className="integrations-header">
        <div>
          <p className="panel__eyebrow">Integrations</p>
          <h1>Source control</h1>
        </div>
        <div className="integrations-header__actions">
          <button className="button" onClick={() => void manager.refresh()} type="button">
            Refresh
          </button>
          <label>
            Connection technical name
            <input
              value={manager.technicalName}
              onChange={(event) => manager.setTechnicalName(event.target.value)}
              disabled={!canManage || manager.actionStatus === 'loading'}
              maxLength={63}
              pattern="[a-z]([a-z0-9-]*[a-z0-9])?"
              placeholder="production-git"
              aria-describedby="connection-name-help"
            />
            <small id="connection-name-help">Immutable, tenant-unique lowercase name.</small>
          </label>
          <button
            className="button button--primary"
            disabled={
              !canManage || !manager.connectionNameValid || manager.actionStatus === 'loading'
            }
            onClick={() => void manager.connectGitHub()}
            type="button"
          >
            Connect GitHub
          </button>
        </div>
      </header>

      {notice && (
        <div
          className={`integration-notice integration-notice--${notice.status}`}
          role={notice.status === 'error' ? 'alert' : 'status'}
        >
          <span>
            {notice.status === 'connected'
              ? 'GitHub installation connected.'
              : (callbackErrors[notice.reason] ?? 'GitHub connection failed.')}
          </span>
          <button onClick={onDismissNotice} type="button">
            Dismiss
          </button>
        </div>
      )}

      <div className="row-actions" aria-label="GitHub repository pages">
        <button
          className="button"
          disabled={manager.repositoryPage === 1 || manager.repositoryStatus === 'loading'}
          onClick={() => manager.selectRepositoryPage(manager.repositoryPage - 1)}
          type="button"
        >
          Previous repositories
        </button>
        <span>Repositories page {manager.repositoryPage}</span>
        <button
          className="button"
          disabled={!manager.hasMoreRepositories || manager.repositoryStatus !== 'success'}
          onClick={() => manager.selectRepositoryPage(manager.repositoryPage + 1)}
          type="button"
        >
          Next repositories
        </button>
        <button
          className="button"
          disabled={manager.branchPage === 1 || manager.branchStatus === 'loading'}
          onClick={() => manager.selectBranchPage(manager.branchPage - 1)}
          type="button"
        >
          Previous branches
        </button>
        <span>Branches page {manager.branchPage}</span>
        <button
          className="button"
          disabled={!manager.hasMoreBranches || manager.branchStatus !== 'success'}
          onClick={() => manager.selectBranchPage(manager.branchPage + 1)}
          type="button"
        >
          Next branches
        </button>
      </div>
      {(manager.error || manager.actionError) && (
        <div className="integration-notice integration-notice--error" role="alert">
          <span>{manager.actionError || manager.error}</span>
        </div>
      )}

      <div className="integrations-metrics" aria-label="Integration summary">
        <div>
          <span>GitHub accounts</span>
          <strong>{manager.connections.length}</strong>
        </div>
        <div>
          <span>Linked sources</span>
          <strong>{manager.sourceRepositories.length}</strong>
        </div>
        <div>
          <span>Selected account</span>
          <strong>{selectedConnection?.accountLogin ?? 'None'}</strong>
        </div>
      </div>

      <div className="integrations-overview-grid">
        <section className="panel integrations-connections">
          <div className="panel__header">
            <div>
              <p className="panel__eyebrow">GitHub App</p>
              <h2 className="panel__title">Connected accounts</h2>
            </div>
            <span className="panel__meta">{manager.connections.length} total</span>
          </div>

          {manager.status === 'loading' && (
            <div className="integration-compact-state">Loading accounts...</div>
          )}
          {manager.status === 'success' && manager.connections.length === 0 && (
            <div className="integration-compact-state">No GitHub accounts connected.</div>
          )}
          <div className="integration-account-list">
            {manager.connections.map((connection) => (
              <div
                className={`integration-account-row${connection.id === manager.selection.connectionId ? ' is-selected' : ''}`}
                key={connection.id}
              >
                <button
                  className="integration-account-row__select"
                  onClick={() => manager.selectConnection(connection.id)}
                  type="button"
                >
                  <span className="integration-provider-mark" aria-hidden="true">
                    GH
                  </span>
                  <span>
                    <strong>{connection.technicalName}</strong>
                    <small>
                      {connection.accountLogin} · {connection.accountType} ·{' '}
                      {connection.repositorySelection} repositories
                    </small>
                  </span>
                </button>
                <button
                  className="button button--danger"
                  disabled={!canManage || manager.actionStatus === 'loading'}
                  onClick={() => void disconnect(connection.id)}
                  type="button"
                >
                  Disconnect
                </button>
              </div>
            ))}
          </div>
        </section>

        <section className="panel integrations-linked">
          <div className="panel__header">
            <div>
              <p className="panel__eyebrow">Pole sources</p>
              <h2 className="panel__title">Linked repositories</h2>
            </div>
            <span className="panel__meta">{manager.sourceRepositories.length} total</span>
          </div>

          {manager.status === 'success' && manager.sourceRepositories.length === 0 && (
            <div className="integration-compact-state">No repositories linked.</div>
          )}
          <div className="integration-source-list">
            {manager.sourceRepositories.map((source) => {
              const busy = manager.busySourceRepositoryId === source.id
              return (
                <div className="integration-source-row" key={source.id}>
                  <div className="integration-source-row__main">
                    <a href={source.repositoryUrl} rel="noreferrer" target="_blank">
                      {source.repositoryFullName}
                    </a>
                    <span>
                      {source.technicalName} · {source.branch}
                      {source.gitOps && ' · Managed by Git'}
                    </span>
                  </div>
                  <code>r{source.revision}</code>
                  <div className="row-actions">
                    <button
                      className="button"
                      disabled={!canManage || busy}
                      onClick={() => void manager.reanalyze(source.id)}
                      type="button"
                    >
                      {busy ? 'Working...' : 'Reanalyze'}
                    </button>
                    <button
                      className="button button--danger"
                      disabled={!canManage || busy || Boolean(source.gitOps)}
                      onClick={() => void unlink(source.id, source.repositoryFullName)}
                      type="button"
                    >
                      Unlink
                    </button>
                  </div>
                </div>
              )
            })}
          </div>
        </section>
      </div>

      <section className="panel repository-workbench">
        <div className="panel__header">
          <div>
            <p className="panel__eyebrow">Repository analysis</p>
            <h2 className="panel__title">Build-plan preview</h2>
          </div>
          <div className="repository-controls">
            <label className="field">
              <span>Repository technical name</span>
              <input
                value={manager.sourceTechnicalName}
                onChange={(event) => manager.setSourceTechnicalName(event.target.value)}
                maxLength={63}
                disabled={!canManage || manager.actionStatus === 'loading'}
                placeholder="application-source"
              />
            </label>
            <button
              className="button button--primary"
              type="button"
              disabled={
                !canManage ||
                !manager.selection.repositoryId ||
                !manager.selection.branch ||
                !manager.sourceNameValid ||
                isLinked ||
                manager.actionStatus === 'loading'
              }
              onClick={() => void manager.link()}
            >
              {isLinked ? 'Already linked' : 'Link repository'}
            </button>
          </div>

          {manager.selection.analysis && (
            <span className="panel__meta">
              {shortSha(manager.selection.analysis.commitSha)} ·{' '}
              {formatDate(manager.selection.analysis.analyzedAtUtc)}
            </span>
          )}
        </div>

        <div className="repository-controls">
          <label className="field">
            <span className="field__label">Account</span>
            <select
              className="field__control"
              disabled={manager.connections.length === 0}
              onChange={(event) => manager.selectConnection(event.target.value)}
              value={manager.selection.connectionId ?? ''}
            >
              <option value="">Select an account</option>
              {manager.connections.map((connection) => (
                <option key={connection.id} value={connection.id}>
                  {connection.accountLogin}
                </option>
              ))}
            </select>
          </label>

          <label className="field">
            <span className="field__label">Repository</span>
            <select
              className="field__control"
              disabled={!manager.selection.connectionId || manager.repositoryStatus === 'loading'}
              onChange={(event) => manager.selectRepository(event.target.value)}
              value={manager.selection.repositoryId ?? ''}
            >
              <option value="">
                {manager.repositoryStatus === 'loading'
                  ? 'Loading repositories...'
                  : 'Select a repository'}
              </option>
              {manager.repositories.map((repository) => (
                <option key={repository.id} value={repository.id}>
                  {repository.fullName}
                </option>
              ))}
            </select>
          </label>

          <label className="field">
            <span className="field__label">Branch</span>
            <select
              className="field__control"
              disabled={!selectedRepository || manager.branchStatus === 'loading'}
              onChange={(event) => manager.selectBranch(event.target.value)}
              value={manager.selection.branch}
            >
              {!manager.selection.branch && <option value="">Select a branch</option>}
              {manager.selection.branch &&
                !manager.branches.some((branch) => branch.name === manager.selection.branch) && (
                  <option value={manager.selection.branch}>{manager.selection.branch}</option>
                )}
              {manager.branches.map((branch) => (
                <option key={branch.name} value={branch.name}>
                  {branch.name}
                  {branch.isProtected ? ' · protected' : ''}
                </option>
              ))}
            </select>
          </label>

          <button
            className="button button--primary repository-controls__action"
            disabled={
              !manager.selection.repositoryId ||
              !manager.selection.branch ||
              manager.actionStatus === 'loading'
            }
            onClick={() => void manager.analyze()}
            type="button"
          >
            {manager.actionStatus === 'loading' ? 'Analyzing...' : 'Analyze'}
          </button>
        </div>

        {manager.repositoryStatus === 'success' &&
          manager.selection.connectionId &&
          manager.repositories.length === 0 && (
            <div className="integration-compact-state">
              No repositories available to this installation.
            </div>
          )}

        <div className="repository-controls">
          <label className="field">
            <span>Repository technical name</span>
            <input
              value={manager.sourceTechnicalName}
              onChange={(event) => manager.setSourceTechnicalName(event.target.value)}
              maxLength={63}
              disabled={!canManage || manager.actionStatus === 'loading'}
              placeholder="application-source"
            />
          </label>
          <button
            className="button button--primary"
            type="button"
            disabled={
              !canManage ||
              !manager.selection.repositoryId ||
              !manager.selection.branch ||
              !manager.sourceNameValid ||
              isLinked ||
              manager.actionStatus === 'loading'
            }
            onClick={() => void manager.link()}
          >
            {isLinked ? 'Already linked' : 'Link repository'}
          </button>
        </div>

        {manager.selection.analysis && (
          <div className="analysis-workspace">
            <div className="analysis-candidates" role="radiogroup" aria-label="Build plans">
              <div className="analysis-candidates__header">
                <strong>Detected candidates</strong>
                <span>{manager.selection.analysis.candidates.length}</span>
              </div>
              {manager.selection.analysis.candidates.length === 0 && (
                <div className="integration-compact-state">No deployable project was detected.</div>
              )}
              {manager.selection.analysis.candidates.map((candidate) => (
                <label
                  className={`analysis-candidate${candidate.candidateKey === manager.selection.candidateKey ? ' is-selected' : ''}`}
                  key={candidate.candidateKey}
                >
                  <input
                    checked={candidate.candidateKey === manager.selection.candidateKey}
                    name="build-plan"
                    onChange={() => manager.selectCandidate(candidate.candidateKey)}
                    type="radio"
                  />
                  <span className="analysis-candidate__body">
                    <span className="analysis-candidate__title">
                      <strong>{candidate.displayName}</strong>
                      <small
                        className={`confidence confidence--${candidate.confidence.toLowerCase()}`}
                      >
                        {candidate.confidence}
                      </small>
                    </span>
                    <code>{candidate.manifestPath}</code>
                    <small>{candidate.signals.join(' · ')}</small>
                  </span>
                </label>
              ))}
            </div>

            <div className="analysis-preview">
              {selectedPlan ? (
                <>
                  <div className="analysis-preview__toolbar">
                    <div className="segmented-control" aria-label="Preview mode">
                      <button
                        className={previewMode === 'dockerfile' ? 'is-active' : ''}
                        onClick={() => setPreviewMode('dockerfile')}
                        type="button"
                      >
                        Dockerfile
                      </button>
                      <button
                        className={previewMode === 'commands' ? 'is-active' : ''}
                        onClick={() => setPreviewMode('commands')}
                        type="button"
                      >
                        Commands
                      </button>
                    </div>
                    <span>
                      {selectedPlan.kind} · {selectedPlan.sourceRoot}
                    </span>
                  </div>

                  {selectedPlan.warnings.length > 0 && (
                    <ul className="analysis-warnings">
                      {selectedPlan.warnings.map((warning) => (
                        <li key={warning}>{warning}</li>
                      ))}
                    </ul>
                  )}

                  <pre className="build-preview" aria-label="Read-only build plan">
                    {previewMode === 'dockerfile'
                      ? selectedPlan.dockerfilePreview
                      : [
                          ...selectedPlan.commands,
                          ...(selectedPlan.startCommand
                            ? [`Start: ${selectedPlan.startCommand}`]
                            : []),
                        ].join('\n')}
                  </pre>

                  <div className="analysis-preview__actions">
                    <span>
                      Branch <strong>{manager.selection.analysis.branch}</strong> at{' '}
                      <code>{shortSha(manager.selection.analysis.commitSha)}</code>
                    </span>

                    {manager.analysisStale && (
                      <p role="status">
                        The branch changed after analysis. Analyze it again before linking this
                        plan.
                      </p>
                    )}
                  </div>
                </>
              ) : (
                <div className="integration-compact-state">
                  {manager.selection.analysis.candidates.length > 1
                    ? 'Select one build-plan candidate.'
                    : 'Select a build-plan candidate.'}
                </div>
              )}
            </div>
          </div>
        )}
      </section>
    </div>
  )
}
