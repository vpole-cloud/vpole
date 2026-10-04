import { useEffect, useState } from 'react'
import { ConfirmationDialog } from '../ui/components'
import { ResourceLink } from '../ui/ResourceLink'
import { GitSourcePicker } from '../modules/web-services/GitSourcePicker'
import { useAppServices } from '../shell/AppContext'
import { BuildRecipeForm } from './BuildRecipeForm'
import { SourceBuildLogViewer } from './SourceBuildLogViewer'
import type { SourceBuildsManager } from '../hooks/useSourceBuildsManager'
import type { SourceBuildJob, SourceRepository, WebService } from '../types'

interface SourceBuildPanelProps {
  webService: WebService
  manager: SourceBuildsManager
  canManage: boolean
  onDirtyChange?: (dirty: boolean) => void
  active?: boolean
}

const shortSha = (value: string) => value.slice(0, 10)
const recipeKinds = ['Dockerfile', '.NET', 'Node.js'] as const
const buildPhases = ['Queued', 'Cloning', 'Analyzing', 'Building', 'Pushing', 'Deploying'] as const
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
    return shortDigest(job.state.imageDigest) ?? 'Build and deployment application completed.'
  }
  return job.state.phase != null ? buildPhases[job.state.phase] : job.state.status
}

export function SourceBuildPanel({
  webService,
  manager,
  canManage,
  onDirtyChange,
  active = true,
}: SourceBuildPanelProps) {
  const { navigation } = useAppServices()
  const deploymentResource = { resourceTypeId: 'pole/web-service', resourceId: webService.id, section: 'deployments' }
  const [selectedSource, setSelectedSource] = useState<SourceRepository | null>(null)
  const [removing, setRemoving] = useState(false)
  const [recipeDirty, setRecipeDirty] = useState(false)
  const [logJobId, setLogJobId] = useState<string | null>(() => /^#build-([a-f0-9]{24})$/i.exec(window.location.hash)?.[1] ?? null)
  useEffect(() => {
    const changed = () => setLogJobId(/^#build-([a-f0-9]{24})$/i.exec(window.location.hash)?.[1] ?? null)
    window.addEventListener('hashchange', changed)
    return () => window.removeEventListener('hashchange', changed)
  }, [])
  const selectLog = (jobId: string | null) => {
    setLogJobId(jobId)
    const url = new URL(window.location.href)
    if (jobId) url.hash = `build-${jobId}`
    else if (url.hash.startsWith('#build-')) url.hash = ''
    window.history.replaceState(window.history.state, '', url)
  }
  const configuration = manager.configurations.find(
    (item) => item.webServiceId === webService.id,
  )
  const source = manager.sourceRepositories.find(
    (item) => item.id === configuration?.sourceRepositoryId,
  )
  const busy = manager.actionStatus === 'loading'
  const dirty = (!configuration && Boolean(selectedSource)) || recipeDirty
  useEffect(() => { onDirtyChange?.(dirty) }, [dirty, onDirtyChange])

  return (
    <section className="panel source-build-panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">Delivery</p>
          <h2 className="panel__title">Source builds</h2>
        </div>
        <div className="row-actions">
          <span className="panel__meta">
            {configuration ? recipeKinds[configuration.recipe.kind] : 'Not configured'}
          </span>
          <button className="button" onClick={() => void manager.refresh()} type="button">
            Refresh
          </button>
        </div>
      </div>

      {(manager.error || manager.actionError || manager.jobsError) && (
        <div className="form-message form-message--error" role="alert">
          {manager.actionError || manager.error || manager.jobsError}
        </div>
      )}

      {manager.status === 'loading' && !configuration ? (
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
            {source && <button className="button" disabled={!canManage || busy || manager.hasActiveBuild}
              onClick={() => void manager.reanalyze(source.id).catch(() => {})} type="button">Analyze latest commit</button>}
            <button
              className="button button--primary"
              disabled={!canManage || busy || manager.hasActiveBuild}
              onClick={() => void manager.start(configuration.id).catch(() => {})}
              type="button"
            >
              {manager.hasActiveBuild ? 'Build in progress' : 'Build and deploy'}
            </button>
            <button
              className="button button--danger"
              disabled={!canManage || busy || manager.hasActiveBuild}
              onClick={() => setRemoving(true)}
              type="button"
            >
              Remove configuration
            </button>
          </div>
          <BuildRecipeForm key={configuration.id} configuration={configuration} canManage={canManage} busy={busy}
            onSave={(recipe) => manager.updateRecipe(configuration.id, recipe)} onDirtyChange={setRecipeDirty} />
        </div>
      ) : (
        <div className="source-build-setup">
          {canManage ? <GitSourcePicker source={selectedSource} onSelect={setSelectedSource} disabled={busy} /> :
            <p>No source is configured. Tenant viewer access is read-only.</p>}
          <button
            className="button button--primary"
            disabled={!canManage || busy || !selectedSource}
            onClick={() => void manager.create(selectedSource!.id, webService.id).then(() => setSelectedSource(null)).catch(() => {})}
            type="button"
          >
            Configure source builds
          </button>
        </div>
      )}

      <div className="source-build-history">
        <p className="field__hint">Build progress and deployment reconciliation are separate. A successful build does not establish application readiness.</p>
        <ResourceLink href={navigation.resourceHref(deploymentResource)} onNavigate={() => navigation.openResource(deploymentResource)}>Inspect deployment results</ResourceLink>
        {active && logJobId && <SourceBuildLogViewer jobId={logJobId} webServiceId={webService.id} onClose={() => selectLog(null)} />}
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
            <article className="source-build-row" key={job.id} id={`build-${job.id}`}>
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
                {job.state.failureCode && <code>{job.state.failureCode}</code>}
                <button className="button" type="button" aria-label={`View logs for build ${job.id}`} onClick={() => selectLog(job.id)}>View logs</button>
                {job.state.status === 'failed' && <span className="field__hint">Check the source recipe and reported failure. Correct the cause, then use Build and deploy to retry.</span>}
              </div>
            </article>
          ))}
        </div>
      </div>
      {removing && configuration && <ConfirmationDialog title="Remove source configuration?" confirmLabel="Remove configuration" busy={busy}
        onCancel={() => setRemoving(false)} onConfirm={() => void manager.remove(configuration.id).then(() => setRemoving(false)).catch(() => {})}>
        <p>{webService.name} keeps its current image and build history.</p></ConfirmationDialog>}
    </section>
  )
}
