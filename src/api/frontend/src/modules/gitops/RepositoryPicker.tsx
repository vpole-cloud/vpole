import { useState } from 'react'
import {
  fetchGitProviderConnections,
  fetchProviderRepositories,
  fetchProviderBranches,
} from '../platform/gitApi'
import { Button, Field, Notice } from '../../ui/components'
import { useAppServices } from '../../shell/AppContext'
import { useGitOpsQuery } from './useGitOpsQuery'
import { safeGitOpsError } from './api'
import { PageControls } from './PageControls'
import type { RepositoryLocation } from './types'
export function RepositoryPicker({
  value,
  onChange,
  active,
  disabled = false,
}: {
  value: RepositoryLocation
  onChange: (value: RepositoryLocation) => void
  active: boolean
  disabled?: boolean
}) {
  const { navigation } = useAppServices()
  const [repositoryPage, setRepositoryPage] = useState({ owner: value.connectionId, page: 1 })
  const [branchPage, setBranchPage] = useState({
    owner: value.connectionId + '/' + value.externalRepositoryId,
    page: 1,
  })
  const currentRepositoryPage =
    repositoryPage.owner === value.connectionId ? repositoryPage.page : 1
  const branchOwner = value.connectionId + '/' + value.externalRepositoryId
  const currentBranchPage = branchPage.owner === branchOwner ? branchPage.page : 1
  const connections = useGitOpsQuery(
    ['picker', 'connections'],
    active,
    false,
    async (api, signal) => {
      try {
        return await fetchGitProviderConnections(
          api.baseUrl,
          api.credential(),
          api.tenantId,
          signal,
        )
      } catch (error) {
        throw safeGitOpsError(error)
      }
    },
  )
  const repositories = useGitOpsQuery(
    ['picker', 'repositories', value.connectionId, currentRepositoryPage],
    active && Boolean(value.connectionId),
    false,
    async (api, signal) => {
      try {
        return await fetchProviderRepositories(
          api.baseUrl,
          value.connectionId,
          api.credential(),
          api.tenantId,
          signal,
          currentRepositoryPage,
        )
      } catch (error) {
        throw safeGitOpsError(error)
      }
    },
  )
  const branches = useGitOpsQuery(
    ['picker', 'branches', value.connectionId, value.externalRepositoryId, currentBranchPage],
    active && Boolean(value.connectionId && value.externalRepositoryId),
    false,
    async (api, signal) => {
      try {
        return await fetchProviderBranches(
          api.baseUrl,
          value.connectionId,
          value.externalRepositoryId,
          api.credential(),
          api.tenantId,
          signal,
          currentBranchPage,
        )
      } catch (error) {
        throw safeGitOpsError(error)
      }
    },
  )
  const error = connections.error || repositories.error || branches.error
  return (
    <fieldset disabled={disabled}>
      <legend>GitHub repository</legend>
      {error && <Notice tone="error">{error}</Notice>}
      {connections.resource?.length === 0 && (
        <Notice>
          Connect a GitHub App installation in Integrations first.{' '}
          <Button onClick={() => navigation.goTo('integrations')}>Open GitHub connections</Button>
        </Notice>
      )}
      <Field label="GitHub connection">
        <select
          className="field__control"
          required
          value={value.connectionId}
          onChange={(event) =>
            onChange({
              ...value,
              connectionId: event.target.value,
              externalRepositoryId: '',
              branch: '',
            })
          }
        >
          <option value="">Choose connection</option>
          {connections.resource?.map((item) => (
            <option key={item.id} value={item.id}>
              {item.technicalName} — {item.accountLogin}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Manifest repository">
        <select
          className="field__control"
          required
          disabled={!value.connectionId}
          value={value.externalRepositoryId}
          onChange={(event) => {
            const selected = repositories.resource?.items.find(
              (item) => item.id === event.target.value,
            )
            onChange({
              ...value,
              externalRepositoryId: event.target.value,
              branch: selected?.defaultBranch ?? '',
            })
          }}
        >
          <option value="">Choose repository</option>
          {value.externalRepositoryId &&
            !repositories.resource?.items.some(
              (item) => item.id === value.externalRepositoryId,
            ) && (
              <option value={value.externalRepositoryId}>
                Selected repository {value.externalRepositoryId}
              </option>
            )}
          {repositories.resource?.items.map((item) => (
            <option key={item.id} value={item.id}>
              {item.fullName}
            </option>
          ))}
        </select>
      </Field>
      {value.connectionId && (
        <PageControls
          label="repositories"
          page={currentRepositoryPage}
          value={repositories.resource}
          onPage={(page) => setRepositoryPage({ owner: value.connectionId, page })}
        />
      )}
      <Field label="Branch">
        <select
          className="field__control"
          required
          disabled={!value.externalRepositoryId}
          value={value.branch}
          onChange={(event) => onChange({ ...value, branch: event.target.value })}
        >
          <option value="">Choose branch</option>
          {value.branch && !branches.resource?.items.some((item) => item.name === value.branch) && (
            <option value={value.branch}>{value.branch}</option>
          )}
          {branches.resource?.items.map((item) => (
            <option key={item.name} value={item.name}>
              {item.name}
            </option>
          ))}
        </select>
      </Field>
      {value.externalRepositoryId && (
        <PageControls
          label="branches"
          page={currentBranchPage}
          value={branches.resource}
          onPage={(page) => setBranchPage({ owner: branchOwner, page })}
        />
      )}
      <Field label="Manifest directory">
        <input
          className="field__control"
          maxLength={1024}
          value={value.manifestDirectory}
          placeholder="Repository root"
          onChange={(event) => onChange({ ...value, manifestDirectory: event.target.value })}
        />
      </Field>
    </fieldset>
  )
}
