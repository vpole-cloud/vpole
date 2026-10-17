import type { PostgresCluster, PostgresUpdateInput } from '../../types'

export interface PostgresDraft {
  instances: number
  storage: number
  computeProfileId: string
}
export interface PostgresConfigurationState {
  baseline: { revision: number; instances: number; storage: number }
  draft: PostgresDraft
  accepted: boolean
}

export function postgresConfiguration(cluster: PostgresCluster): PostgresConfigurationState {
  const { instances, storage } = cluster.spec
  return {
    baseline: { revision: cluster.revision, instances, storage },
    draft: { instances, storage, computeProfileId: '' },
    accepted: false,
  }
}

export function postgresChanges(state: PostgresConfigurationState): PostgresUpdateInput {
  const { baseline, draft } = state
  return {
    expectedRevision: baseline.revision,
    ...(draft.instances !== baseline.instances ? { instances: draft.instances } : {}),
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
    },
    draft: { ...state.draft, computeProfileId: '' },
    accepted: true,
  }
}
