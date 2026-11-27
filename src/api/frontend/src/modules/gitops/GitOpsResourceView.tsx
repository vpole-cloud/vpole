// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useEffect, useState } from 'react'
import './gitops.css'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { useAppServices } from '../../shell/AppContext'
import { Button, EmptyState, Notice } from '../../ui/components'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import { Tabs, TabPanel } from '../../ui/Tabs'
import { fetchSource } from './api'
import { useGitOpsQuery } from './useGitOpsQuery'
import { sections, sourceInterval, sourceLabel, type GitOpsSection } from './model'
import { referenceParams } from './navigation'
import { SourceOverview } from './SourceOverview'
import { SourceResources } from './SourceResources'
import { SourceRuns } from './SourceRuns'
import { SourceSettings } from './SourceSettings'
import { GitOpsPreviews } from './GitOpsPreviews'
import type { GitOpsSource } from './types'
export function GitOpsResourceView({
  id,
  section,
  active,
  onSection,
  actions,
}: {
  id: string
  section: GitOpsSection
  active: boolean
  onSection: (section: GitOpsSection) => void
  actions: WorkspaceActions<GitOpsSection>
}) {
  const { navigation, tenant } = useAppServices()
  const query = useGitOpsQuery<GitOpsSource>(
    ['source', id],
    active,
    sourceInterval,
    (api, signal) => fetchSource(api, id, signal),
  )
  const [visited, setVisited] = useState(() => new Set<GitOpsSection>([section]))
  if (!visited.has(section)) setVisited(new Set([...visited, section]))
  const source = query.resource
  const healthy = hasHealthyCurrentQueryData(query)
  const label = source ? sourceLabel(source) : ''
  const loaded = actions.loaded
  useEffect(() => {
    if (label) loaded(id, label)
  }, [loaded, id, label])
  const onRun = (runId: string) =>
    navigation.setSearch(referenceParams(navigation.search, id, 'run', runId))
  const onPreview = (previewId?: string) => {
    if (previewId)
      navigation.setSearch(referenceParams(navigation.search, id, 'preview', previewId))
    else {
      const params = new URLSearchParams(navigation.search)
      params.set('section', 'previews')
      params.delete('preview')
      params.delete('run')
      params.delete('selectionSource')
      navigation.setSearch(params)
    }
  }
  const refreshSource = () => {
    void query.refresh()
  }
  const onDeleted = () => actions.closed(id)
  if (!source)
    return (
      <section className="panel">
        <EmptyState
          title={
            query.status === 'error' ? 'Source unavailable in this tenant.' : 'Loading source…'
          }
          error={query.status === 'error'}
        />
        {query.error && <Notice tone="error">{query.error}</Notice>}
        <Button onClick={refreshSource}>Refresh source</Button>
      </section>
    )
  const workspace = `gitops-source-${id}`
  return (
    <article className="gitops-source" data-testid={`gitops-source-${id}`}>
      <header className="ui-page-header">
        <div>
          <p className="ui-page-eyebrow">GitOps source</p>
          <h1>{label}</h1>
          <p>
            <code>{id}</code>
          </p>
        </div>
        <Button onClick={refreshSource}>Refresh source</Button>
      </header>
      {query.error && (
        <Notice tone="error">
          {query.error} Loaded metadata remains visible; commands require a successful current read.
        </Notice>
      )}
      {!tenant.canManage && <Notice>Tenant viewer access is read-only.</Notice>}
      <Tabs
        id={workspace}
        label="GitOps source sections"
        variant="section"
        activeId={section}
        onSelect={(next) => onSection(next as GitOpsSection)}
        tabs={sections.map((value) => ({
          id: value,
          label: value[0].toUpperCase() + value.slice(1),
        }))}
      />
      {visited.has('overview') && (
        <TabPanel id={workspace} tabId="overview" activeId={section}>
          <SourceOverview
            source={source}
            active={active && section === 'overview'}
            healthy={healthy}
            onRun={onRun}
            onPreview={onPreview}
            onDeleted={onDeleted}
            refreshSource={refreshSource}
          />
        </TabPanel>
      )}
      {visited.has('resources') && (
        <TabPanel id={workspace} tabId="resources" activeId={section}>
          <SourceResources
            source={source}
            active={active && section === 'resources'}
            onRun={onRun}
          />
        </TabPanel>
      )}
      {visited.has('runs') && (
        <TabPanel id={workspace} tabId="runs" activeId={section}>
          <SourceRuns
            source={source}
            active={active && section === 'runs'}
            healthy={healthy}
            onRun={onRun}
            onDeleted={onDeleted}
            refreshSource={refreshSource}
          />
        </TabPanel>
      )}
      {visited.has('previews') && (
        <TabPanel id={workspace} tabId="previews" activeId={section}>
          <GitOpsPreviews
            source={source}
            active={active && section === 'previews'}
            healthy={healthy}
            onRun={onRun}
          />
        </TabPanel>
      )}
      {visited.has('settings') && (
        <TabPanel id={workspace} tabId="settings" activeId={section}>
          <SourceSettings
            source={source}
            active={active && section === 'settings'}
            healthy={healthy}
            actions={actions}
          />
        </TabPanel>
      )}
    </article>
  )
}
