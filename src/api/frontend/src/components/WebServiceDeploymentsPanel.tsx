// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { retryOperationForStatus } from './webServiceDeploymentStatus'
import { useEffect, useMemo, useState } from 'react'
import type {
  AvailableComputeProfile,
  AvailableDeploymentTarget,
  WebService,
  WebServiceDeploymentInput,
} from '../types'

interface WebServiceDeploymentsPanelProps {
  webService: WebService
  availableTargets: AvailableDeploymentTarget[]
  computeProfilesByTarget: Record<string, AvailableComputeProfile[]>
  canManage: boolean
  onDirtyChange?: (dirty: boolean) => void
  onLoadComputeProfiles: (targetId: string) => Promise<AvailableComputeProfile[]>
  onAddDeployment: (id: string, input: WebServiceDeploymentInput) => Promise<unknown>
  onUpdateDeployment: (
    id: string,
    deploymentId: string,
    input: { fixedReplicas: number; computeProfileId?: string },
  ) => Promise<unknown>
  onRemoveDeployment: (id: string, deploymentId: string) => Promise<unknown>
  onRetryDeployment: (
    id: string,
    deploymentId: string,
    expectedDeploymentRevision: number,
    expectedOperation?: 'Apply' | 'Delete',
  ) => Promise<unknown>
}

const statusClass = (status: string) =>
  status.toLowerCase().includes('fail') || status.toLowerCase().includes('error')
    ? 'status-pill--danger'
    : status.toLowerCase().includes('pending')
      ? 'status-pill--accent'
      : 'status-pill--success'

export function WebServiceDeploymentsPanel({
  webService,
  availableTargets,
  computeProfilesByTarget,
  canManage,
  onDirtyChange,
  onLoadComputeProfiles,
  onAddDeployment,
  onUpdateDeployment,
  onRemoveDeployment,
  onRetryDeployment,
}: WebServiceDeploymentsPanelProps) {
  const [addTargetId, setAddTargetId] = useState('')
  const [addReplicas, setAddReplicas] = useState('1')
  const [addComputeProfileId, setAddComputeProfileId] = useState('')
  const dirty = Boolean(addTargetId || addComputeProfileId || addReplicas !== '1')
  useEffect(() => {
    onDirtyChange?.(dirty)
  }, [dirty, onDirtyChange])
  const run = (action: Promise<unknown>) => {
    void action.catch(() => {})
  }

  useEffect(() => {
    webService.deployments.forEach((deployment) => {
      void onLoadComputeProfiles(deployment.deploymentTargetId)
    })
  }, [onLoadComputeProfiles, webService.deployments, webService.id])

  const unusedTargets = useMemo(
    () =>
      availableTargets.filter(
        (target) =>
          !webService.deployments.some((deployment) => deployment.deploymentTargetId === target.id),
      ),
    [availableTargets, webService.deployments],
  )

  const addProfiles = computeProfilesByTarget[addTargetId] ?? []
  const addTarget = async () => {
    if (!addTargetId) return
    try {
      await onAddDeployment(webService.id, {
        deploymentTargetId: addTargetId,
        fixedReplicas: Number(addReplicas),
        computeProfileId: addComputeProfileId || undefined,
      })
      setAddTargetId('')
      setAddComputeProfileId('')
      setAddReplicas('1')
    } catch {
      /* Keep the draft; the manager exposes the API error. */
    }
  }

  return (
    <section className="panel web-service-deployments">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">Placement</p>
          <h2 className="panel__title">Physical deployments</h2>
        </div>
        <span className="panel__meta">{webService.deployments.length} active</span>
      </div>

      <p className="form-message">
        Each target reconciles independently. A failed target does not block the other deployments
        in this release.
      </p>

      <div className="list">
        {webService.deployments.map((deployment) => {
          const profiles = computeProfilesByTarget[deployment.deploymentTargetId] ?? []
          return (
            <article
              className="list-row"
              key={deployment.deploymentId}
              data-testid={`deployment-row-${deployment.deploymentId}`}
            >
              <div className="list-row__primary">
                <h3>
                  {availableTargets.find((target) => target.id === deployment.deploymentTargetId)
                    ?.displayName ?? deployment.deploymentTargetId}
                </h3>
                <p>
                  Generation {deployment.observedGeneration}/{deployment.desiredGeneration} ·{' '}
                  {deployment.fixedReplicas} fixed replicas
                </p>
                {deployment.failureCode && (
                  <p className="form-message form-message--error">{deployment.failureCode}</p>
                )}
              </div>
              <div className="list-row__secondary">
                <span
                  data-testid="deployment-status"
                  className={`status-pill ${statusClass(deployment.status)}`}
                >
                  {deployment.status}
                </span>
                <span className="list-row__meta">{deployment.compute.displayName}</span>
              </div>

              <div className="form-grid">
                <label className="field">
                  <span className="field__label">Fixed replicas</span>
                  <input
                    className="field__control"
                    disabled={!canManage}
                    type="number"
                    min="1"
                    max="1000"
                    defaultValue={deployment.fixedReplicas}
                    onBlur={(event) => {
                      const fixedReplicas = Number(event.target.value)
                      if (fixedReplicas !== deployment.fixedReplicas && fixedReplicas > 0)
                        run(
                          onUpdateDeployment(webService.id, deployment.deploymentId, {
                            fixedReplicas,
                            computeProfileId: deployment.compute.profile.computeProfileId,
                          }),
                        )
                    }}
                  />
                </label>
                <label className="field">
                  <span className="field__label">Compute profile</span>
                  <select
                    className="field__control"
                    disabled={!canManage}
                    defaultValue={deployment.compute.profile.computeProfileId}
                    onChange={(event) =>
                      run(
                        onUpdateDeployment(webService.id, deployment.deploymentId, {
                          fixedReplicas: deployment.fixedReplicas,
                          computeProfileId: event.target.value || undefined,
                        }),
                      )
                    }
                  >
                    <option value="">Target default</option>
                    {profiles.map((profile) => (
                      <option key={profile.id} value={profile.id}>
                        {profile.displayName}
                      </option>
                    ))}
                  </select>
                </label>
              </div>

              <p className="field__hint">
                Manage exact-target hostname ownership in the Domains section.
              </p>

              <div className="row-actions">
                <button
                  className="button"
                  disabled={!canManage}
                  type="button"
                  onClick={() =>
                    run(
                      onRetryDeployment(
                        webService.id,
                        deployment.deploymentId,
                        deployment.deploymentRevision,
                        retryOperationForStatus(deployment.status),
                      ),
                    )
                  }
                >
                  Retry
                </button>
                <button
                  className="button button--danger"
                  disabled={!canManage || webService.deployments.length <= 1}
                  type="button"
                  onClick={() => {
                    if (
                      window.confirm(
                        'Remove this physical deployment? The last deployment cannot be removed.',
                      )
                    )
                      run(onRemoveDeployment(webService.id, deployment.deploymentId))
                  }}
                >
                  Remove target
                </button>
              </div>
            </article>
          )
        })}
      </div>

      {canManage && unusedTargets.length > 0 && (
        <div className="form-grid">
          <select
            className="field__control"
            value={addTargetId}
            onChange={(event) => {
              setAddTargetId(event.target.value)
              setAddComputeProfileId('')
              if (event.target.value) void onLoadComputeProfiles(event.target.value)
            }}
          >
            <option value="">Add an eligible target</option>
            {unusedTargets.map((target) => (
              <option key={target.id} value={target.id}>
                {target.displayName}
              </option>
            ))}
          </select>
          <input
            className="field__control"
            type="number"
            min="1"
            max="1000"
            value={addReplicas}
            onChange={(event) => setAddReplicas(event.target.value)}
          />
          <select
            className="field__control"
            value={addComputeProfileId}
            onChange={(event) => setAddComputeProfileId(event.target.value)}
          >
            <option value="">Target default compute</option>
            {addProfiles.map((profile) => (
              <option key={profile.id} value={profile.id}>
                {profile.displayName}
              </option>
            ))}
          </select>
          <button
            className="button button--primary"
            type="button"
            disabled={!addTargetId}
            onClick={() => void addTarget()}
          >
            Add target
          </button>
        </div>
      )}
    </section>
  )
}
