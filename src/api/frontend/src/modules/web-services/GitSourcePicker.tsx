import { useState } from 'react'
import { useAppServices } from '../../shell/AppContext'
import type { SourceRepository } from '../../types'
import { Button, EmptyState, Notice } from '../../ui/components'
import { useGitSourceSelection } from './useGitSourceSelection'

export function GitSourcePicker({ source, onSelect, disabled = false }: {
  source: SourceRepository | null; onSelect: (source: SourceRepository | null) => void; disabled?: boolean
}) {
  const { api, tenant, navigation } = useAppServices()
  const manager = useGitSourceSelection(api, !disabled && tenant.canManage)
  const [chooseNew, setChooseNew] = useState(false)
  const plan = manager.selection.analysis?.candidates.find((item) => item.candidateKey === manager.selection.candidateKey)
  const readOnly = disabled || !tenant.canManage
  return <section className="panel git-source-picker">
    <div className="panel__header"><div><p className="panel__eyebrow">Git source</p><h2 className="panel__title">Repository and build plan</h2></div>
      <Button disabled={manager.busy} onClick={manager.refresh}>Refresh sources</Button></div>
    {manager.error && <Notice tone="error">{manager.error}</Notice>}
    {manager.status === 'loading' && <p role="status">Loading GitHub accounts and sources…</p>}
    {manager.status === 'error' && <Button onClick={manager.refresh}>Retry sources</Button>}
    <label className="field"><span className="field__label">Linked source</span><select className="field__control"
      value={source?.id ?? ''} disabled={readOnly || manager.status !== 'success'} onChange={(event) => {
        onSelect(manager.sources.find((item) => item.id === event.target.value) ?? null)
      }}><option value="">Choose a linked source</option>{manager.sources.map((item) => <option key={item.id} value={item.id}>
        {item.repositoryFullName} · {item.branch} · {item.selectedPlan.displayName}</option>)}</select></label>
    {source && <div className="git-source-summary"><strong>{source.repositoryFullName}</strong><span>{source.branch} · commit {source.commitSha.slice(0, 10)}</span>
      <span>{source.selectedPlan.displayName} · {source.selectedPlan.manifestPath}</span>
      <p className="field__hint">Builds use this analyzed commit. Reanalyze the linked source to build a newer commit.</p></div>}
    {!readOnly && <Button onClick={() => setChooseNew((value) => !value)}>{chooseNew ? 'Hide repository selection' : 'Choose another repository'}</Button>}
    {(chooseNew || (manager.status === 'success' && manager.sources.length === 0)) && <fieldset disabled={readOnly || manager.busy} className="web-service-form__fields">
      {manager.status === 'success' && manager.connections.length === 0 ? <EmptyState title="Connect a GitHub account first.">
        <p>A tenant administrator must connect a GitHub App installation with repository contents access.</p>
        <Button onClick={() => navigation.goTo('integrations')}>Open GitHub connections</Button></EmptyState> : <>
        <div className="form-grid form-grid--three">
          <label className="field"><span className="field__label">GitHub account</span><select className="field__control" value={manager.selection.connectionId ?? ''}
            onChange={(event) => manager.selectConnection(event.target.value)}><option value="">Choose an account</option>
            {manager.connections.map((item) => <option key={item.id} value={item.id}>{item.accountLogin}</option>)}</select></label>
          <label className="field"><span className="field__label">Repository</span><select className="field__control" value={manager.selection.repositoryId ?? ''}
            disabled={manager.repositoryStatus !== 'success'} onChange={(event) => manager.selectRepository(event.target.value)}><option value="">Choose a repository</option>
            {manager.repositories.map((item) => <option key={item.id} value={item.id}>{item.fullName}{item.isPrivate ? ' · private' : ''}</option>)}</select></label>
          <label className="field"><span className="field__label">Branch</span><select className="field__control" value={manager.selection.branch}
            disabled={manager.branchStatus !== 'success'} onChange={(event) => manager.selectBranch(event.target.value)}><option value="">Choose a branch</option>
            {manager.branches.map((item) => <option key={item.name} value={item.name}>{item.name}</option>)}</select></label>
        </div>
        {manager.repositoryStatus === 'loading' && <p role="status">Loading repositories…</p>}
        {manager.branchStatus === 'loading' && <p role="status">Loading branches…</p>}
        {manager.repositoryStatus === 'success' && manager.repositories.length === 0 && <Notice>No repositories are accessible. Check the GitHub App repository selection in Integrations.</Notice>}
        {manager.branchStatus === 'success' && manager.branches.length === 0 && <Notice>No branches are available for this repository.</Notice>}
        <div className="row-actions">{manager.moreRepositories && <Button disabled={manager.repositoryStatus !== 'success'} onClick={manager.loadMoreRepositories}>More repositories</Button>}
          {manager.moreBranches && <Button disabled={manager.branchStatus !== 'success'} onClick={manager.loadMoreBranches}>More branches</Button>}
          <Button disabled={!manager.selection.repositoryId || !manager.selection.branch || manager.branchStatus !== 'success' || !manager.branches.some((item) => item.name === manager.selection.branch)} onClick={() => void manager.analyze()}>{manager.busy ? 'Working…' : 'Analyze branch'}</Button></div>
        {manager.selection.analysis && <>
          <p className="field__hint">Analyzed commit {manager.selection.analysis.commitSha.slice(0, 10)}. Choose how this application will be built.</p>
          {manager.selection.analysis.candidates.length === 0 ? <Notice>No supported build plan was detected. Add a Dockerfile or a supported .NET/Node project, then analyze again.</Notice> :
            <label className="field"><span className="field__label">Build plan</span><select className="field__control" value={manager.selection.candidateKey ?? ''}
              onChange={(event) => manager.selectCandidate(event.target.value)}><option value="">Choose a build plan</option>
              {manager.selection.analysis.candidates.map((item) => <option key={item.candidateKey} value={item.candidateKey}>{item.displayName} · {item.manifestPath}</option>)}</select></label>}
          {plan && <><div className="git-source-summary"><span>{plan.kind} · {plan.confidence} confidence</span>
            {plan.warnings.map((warning, index) => <Notice key={index}>{warning}</Notice>)}
            <details><summary>Build recipe preview</summary><pre className="git-recipe-preview">{plan.dockerfilePreview}</pre></details></div>
            <Button variant="primary" onClick={() => void manager.link().then((linked) => { if (linked) { onSelect(linked); setChooseNew(false) } })}>Use this source</Button></>}
        </>}
      </>}
    </fieldset>}
  </section>
}
