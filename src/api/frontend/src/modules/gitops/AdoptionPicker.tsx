// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useState } from 'react'
import { hasHealthyCurrentQueryData } from '../../data/hasHealthyCurrentQueryData'
import { Button, DataTable, Field, Notice } from '../../ui/components'
import { fetchCandidates } from './api'
import { safeCode } from './model'
import { PageControls } from './PageControls'
import type { CandidateSearch, ResourceIdentity } from './types'
import { useGitOpsQuery } from './useGitOpsQuery'
const kinds = [
  'ResourceGroup',
  'IsolationScope',
  'WebService',
  'PostgresCluster',
  'RedisService',
  'FunctionApp',
  'ResourceConnection',
  'DomainZone',
  'HostnameBinding',
  'Secret',
  'SecretBinding',
  'SourceRepository',
  'SourceBuildConfiguration',
]
export function AdoptionPicker({
  sourceId,
  active,
  disabled,
  selected,
  onChange,
}: {
  sourceId: string
  active: boolean
  disabled: boolean
  selected: ResourceIdentity[]
  onChange: (resources: ResourceIdentity[]) => void
}) {
  const [kind, setKind] = useState('ResourceGroup')
  const [name, setName] = useState('')
  const [secret, setSecret] = useState('')
  const [consumerKind, setConsumerKind] = useState('WebService')
  const [consumer, setConsumer] = useState('')
  const [declarationName, setDeclarationName] = useState('')
  const [search, setSearch] = useState<CandidateSearch | null>(null)
  const [page, setPage] = useState(1)
  const binding = kind === 'SecretBinding'
  const draftSearch: CandidateSearch = binding
    ? {
        kind,
        relatedKind: 'Secret',
        relatedName: secret.trim(),
        consumerKind,
        consumerName: consumer.trim(),
      }
    : { kind, name: name.trim() }
  const currentSearch = JSON.stringify(search) === JSON.stringify(draftSearch)
  const candidates = useGitOpsQuery(
    ['adoption-candidates', sourceId, search, page],
    active && search !== null && !disabled,
    false,
    (api, signal) => fetchCandidates(api, { sourceId, search: search!, page }, signal),
  )
  const current = currentSearch && hasHealthyCurrentQueryData(candidates)
  const ready = binding ? Boolean(secret.trim() && consumer.trim()) : Boolean(name.trim())
  return (
    <section aria-label="Find manual resources">
      <h4>Find a manual resource</h4>
      <p>
        Search identifies existing resources. The server preview checks declaration compatibility
        and ownership again.
      </p>
      <Field label="Resource kind">
        <select
          className="field__control"
          value={kind}
          disabled={disabled}
          onChange={(event) => {
            setKind(event.target.value)
            setSearch(null)
            setPage(1)
          }}
        >
          {kinds.map((value) => (
            <option key={value}>{value}</option>
          ))}
        </select>
      </Field>
      {binding ? (
        <>
          <Field label="Secret technical name">
            <input
              className="field__control"
              value={secret}
              disabled={disabled}
              onChange={(event) => setSecret(event.target.value)}
            />
          </Field>
          <Field label="Consumer kind">
            <select
              className="field__control"
              value={consumerKind}
              disabled={disabled}
              onChange={(event) => setConsumerKind(event.target.value)}
            >
              {['WebService', 'FunctionApp', 'PostgresCluster'].map((value) => (
                <option key={value}>{value}</option>
              ))}
            </select>
          </Field>
          <Field label="Consumer technical name">
            <input
              className="field__control"
              value={consumer}
              disabled={disabled}
              onChange={(event) => setConsumer(event.target.value)}
            />
          </Field>
          <Field label="Binding declaration name in Git">
            <input
              className="field__control"
              value={declarationName}
              disabled={disabled}
              onChange={(event) => setDeclarationName(event.target.value)}
            />
          </Field>
        </>
      ) : (
        <Field label="Existing technical name">
          <input
            className="field__control"
            value={name}
            disabled={disabled}
            onChange={(event) => setName(event.target.value)}
          />
        </Field>
      )}
      <Button
        disabled={disabled || !ready}
        onClick={() => {
          setPage(1)
          setSearch(draftSearch)
        }}
      >
        Find adoption candidates
      </Button>
      {candidates.error && <Notice tone="error">{candidates.error}</Notice>}
      {search && !currentSearch && (
        <Notice>Search fields changed. Search again before selecting a candidate.</Notice>
      )}
      {candidates.resource && (
        <>
          <DataTable
            label="Adoption candidates"
            items={candidates.resource.items}
            itemKey={(item) => item.resourceId}
            rowTestId={(item) => `gitops-candidate-${item.resourceId}`}
            columns={[
              {
                id: 'identity',
                label: 'Identity',
                render: (item) => (
                  <>
                    {item.kind}/{item.name ?? '(unnamed binding)'}
                    <p>{item.resourceId}</p>
                  </>
                ),
              },
              {
                id: 'ownership',
                label: 'Ownership',
                render: (item) =>
                  item.ownerSourceId ? `Git source ${item.ownerSourceId}` : 'Manual',
              },
              {
                id: 'binding',
                label: 'Binding metadata',
                render: (item) =>
                  item.binding ? (
                    <>
                      {item.binding.secret.name} → {item.binding.consumer.kind}/
                      {item.binding.consumer.name}
                      <p>
                        {item.binding.usage};{' '}
                        {item.binding.targetScope.mode === 'All'
                          ? 'All deployments'
                          : item.binding.targetScope.deploymentIds.join(', ')}
                      </p>
                    </>
                  ) : (
                    '—'
                  ),
              },
              {
                id: 'availability',
                label: 'Availability',
                render: (item) => (item.blocker ? safeCode(item.blocker) : item.presence),
              },
              {
                id: 'select',
                label: 'Select',
                render: (item) => {
                  const proposedName =
                    item.kind === 'SecretBinding' ? declarationName.trim() : item.name
                  const duplicate = selected.some(
                    (value) =>
                      value.resourceId === item.resourceId ||
                      (value.kind === item.kind && value.name === proposedName),
                  )
                  return (
                    <Button
                      disabled={
                        disabled ||
                        !current ||
                        !item.selectable ||
                        !proposedName ||
                        duplicate ||
                        selected.length >= 256
                      }
                      onClick={() => {
                        if (proposedName && item.selectable && current)
                          onChange([
                            ...selected,
                            { kind: item.kind, name: proposedName, resourceId: item.resourceId },
                          ])
                      }}
                    >
                      Select candidate
                    </Button>
                  )
                },
              },
            ]}
          />
          {candidates.resource.items.length === 0 && (
            <Notice>No selectable resource matched this search.</Notice>
          )}
          <PageControls
            label="adoption candidates"
            page={page}
            value={candidates.resource}
            onPage={setPage}
          />
        </>
      )}
    </section>
  )
}
