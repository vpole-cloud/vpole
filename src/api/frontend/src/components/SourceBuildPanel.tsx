import { useEffect, useState } from 'react'
import type { SourceBuildsManager } from '../hooks/useSourceBuildsManager'
import type { SourceBuildJob, WebService } from '../types'

interface SourceBuildPanelProps {
  webService: WebService
  manager: SourceBuildsManager
  canManage: boolean
  onDirtyChange?: (dirty: boolean) => void
}

const shortSha = (value: string) => value.slice(0, 10)
const shortDigest = (value?: string | null) =>
  value ? `${value.slice(0, 19)}...` : null
const formatDate = (value: string) =>
  new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value))

const buildDetail = (job: SourceBuildJob) => {
  if (job.state.status === 'failed') {
    return job.state.failureMessage ?? 'Build failed.'
  }
  if (job.state.status === 'succeeded') {
    return shortDigest(job.state.imageDigest) ?? 'Image deployed.'
  }
  return job.state.phase ?? job.state.status
}

export function SourceBuildPanel({
  webService,
  manager,
  canManage,
  onDirtyChange,
}: SourceBuildPanelProps) {
  const [sourceRepositoryId, setSourceRepositoryId] = useState('')
  const configuration = manager.configurations.find(
    (item) => item.webServiceId === webService.id,
  )
  const source = manager.sourceRepositories.find(
    (item) => item.id === configuration?.sourceRepositoryId,
  )
  const busy = manager.actionStatus === 'loading'
  const dirty = !configuration && Boolean(sourceRepositoryId)
  useEffect(() => { onDirtyChange?.(dirty) }, [dirty, onDirtyChange])

  const remove = async () => {
    if (
      configuration &&
      window.confirm(`Remove source build configuration from ${webService.name}?`)
    ) {
      await manager.remove(configuration.id)
    }
  }

  return (
    <section className="panel source-build-panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">Delivery</p>
          <h2 className="panel__title">Source builds</h2>
        </div>
        <div className="row-actions">
          <span className="panel__meta">
            {configuration ? configuration.recipe.kind : 'Not configured'}
          </span>
          <button className="button" onClick={() => void manager.refresh()} type="button">
            Refresh
          </button>
        </div>
      </div>

      {(manager.error || manager.actionError) && (
        <div className="form-message form-message--error" role="alert">
          {manager.actionError || manager.error}
        </div>
      )}

      {manager.status === 'loading' ? (
        <div className="integration-compact-state">Loading source configuration...</div>
      ) : configuration ? (
        <div className="source-build-configuration">
          <div className="source-build-configuration__identity">
            <strong>{source?.repositoryFullName ?? configuration.sourceRepositoryId}</strong>
            <span>
              {source?.branch ?? 'Linked branch'} at{' '}
              <code>{source ? shortSha(source.commitSha) : 'unknown'}</code>
            </span>
            <span>
              {configuration.recipe.manifestPath} · {configuration.recipe.candidateKey}
            </span>
          </div>
          <div className="row-actions">
            <button
              className="button button--primary"
              disabled={!canManage || busy || manager.hasActiveBuild}
              onClick={() => void manager.start(configuration.id)}
              type="button"
            >
              {manager.hasActiveBuild ? 'Build in progress' : 'Build and deploy'}
            </button>
            <button
              className="button button--danger"
              disabled={!canManage || busy || manager.hasActiveBuild}
              onClick={() => void remove()}
              type="button"
            >
              Remove configuration
            </button>
          </div>
        </div>
      ) : (
        <div className="source-build-setup">
          <label className="field">
            <span className="field__label">Linked repository</span>
            <select
              className="field__control"
              disabled={!canManage || busy || manager.sourceRepositories.length === 0}
              onChange={(event) => setSourceRepositoryId(event.target.value)}
              value={sourceRepositoryId}
            >
              <option value="">Select a repository</option>
              {manager.sourceRepositories.map((repository) => (
                <option key={repository.id} value={repository.id}>
                  {repository.repositoryFullName} · {repository.branch} ·{' '}
                  {repository.selectedPlan.displayName}
                </option>
              ))}
            </select>
          </label>
          <button
            className="button button--primary"
            disabled={!canManage || busy || !sourceRepositoryId}
            onClick={() => void manager.create(sourceRepositoryId, webService.id).then(() => setSourceRepositoryId('')).catch(() => {})}
            type="button"
          >
            Configure source builds
          </button>
          {manager.sourceRepositories.length === 0 && (
            <span className="field__hint">
              Link and analyze a repository from Integrations first.
            </span>
          )}
        </div>
      )}

      <div className="source-build-history">
        <div className="source-build-history__header">
          <strong>Recent builds</strong>
          <span>{manager.jobs.length}</span>
        </div>
        {manager.jobsStatus === 'loading' && manager.jobs.length === 0 && (
          <div className="integration-compact-state">Loading build history...</div>
        )}
        {manager.jobsStatus === 'success' && manager.jobs.length === 0 && (
          <div className="integration-compact-state">No builds have run for this service.</div>
        )}
        <div className="source-build-list">
          {manager.jobs.map((job) => (
            <article className="source-build-row" key={job.id}>
              <div>
                <strong>{job.state.status}</strong>
                <span>{formatDate(job.createdAtUtc)}</span>
              </div>
              <div>
                <code>{shortSha(job.commitSha)}</code>
                <span>{job.branch}</span>
              </div>
              <div className="source-build-row__result">
                <span className={`build-status build-status--${job.state.status}`}>
                  {job.state.status}
                </span>
                <small title={job.state.failureMessage ?? job.state.imageDigest ?? undefined}>
                  {buildDetail(job)}
                </small>
              </div>
            </article>
          ))}
        </div>
      </div>
    </section>
  )
}
