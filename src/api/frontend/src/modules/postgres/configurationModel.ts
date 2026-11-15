import type { PostgresCluster, PostgresUpdateInput } from '../../types'
import type { PostgresReplicationMode } from './types'

export const postgresReplicationLabels: Record<PostgresReplicationMode, string> = {
  Asynchronous: 'Asynchronous replication',
  SynchronousHa: 'Synchronous HA',
}

export interface PostgresDraft {
  instances: number
  replicationMode: PostgresReplicationMode
  storage: number
  computeProfileId: string
}
export interface PostgresConfigurationState {
  baseline: {
    revision: number
    instances: number
    storage: number
    replicationMode: PostgresReplicationMode
  }
  draft: PostgresDraft
  accepted: boolean
}

export function postgresConfiguration(cluster: PostgresCluster): PostgresConfigurationState {
  const { instances, storage, replicationMode } = cluster.spec
  return {
    baseline: { revision: cluster.revision, instances, storage, replicationMode },
    draft: { instances, storage, replicationMode, computeProfileId: '' },
    accepted: false,
  }
}

export function postgresChanges(state: PostgresConfigurationState): PostgresUpdateInput {
  const { baseline, draft } = state
  return {
    expectedRevision: baseline.revision,
    ...(draft.instances !== baseline.instances ? { instances: draft.instances } : {}),
    ...(draft.replicationMode !== baseline.replicationMode
      ? { replicationMode: draft.replicationMode }
      : {}),
    ...(draft.storage !== baseline.storage ? { storage: draft.storage } : {}),
    ...(draft.computeProfileId ? { computeProfileId: draft.computeProfileId } : {}),
  }
}

export function isPostgresDirty(state: PostgresConfigurationState) {
  return Object.keys(postgresChanges(state)).length > 1
}

export function reapplyPostgresChanges(
  state: PostgresConfigurationState,
  cluster: PostgresCluster,
): PostgresConfigurationState {
  const changes = postgresChanges(state)
  const next = postgresConfiguration(cluster)
  return {
    ...next,
    draft: {
      instances: changes.instances ?? next.draft.instances,
      storage: changes.storage ?? next.draft.storage,
      replicationMode: changes.replicationMode ?? next.draft.replicationMode,
      computeProfileId: changes.computeProfileId ?? '',
    },
  }
}

// An accepted command awaits a newer server revision before this form can submit again.
export function acceptedPostgresChanges(
  state: PostgresConfigurationState,
): PostgresConfigurationState {
  return {
    baseline: {
      revision: state.baseline.revision,
      instances: state.draft.instances,
      storage: state.draft.storage,
      replicationMode: state.draft.replicationMode,
    },
    draft: { ...state.draft, computeProfileId: '' },
    accepted: true,
  }
}
