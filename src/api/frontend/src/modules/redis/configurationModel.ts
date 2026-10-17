import type { RedisService } from './types'
import { parseRedisTags } from './workspaceState'

const signature = (tags: readonly string[]) => JSON.stringify([...new Set(tags)].sort())
export function redisConfiguration(redis: RedisService) {
  return {
    baseline: { revision: redis.revision, tags: signature(redis.tags) },
    draft: { tags: redis.tags.join(', '), computeProfileId: '' },
    accepted: false,
  }
}
export type RedisConfigurationState = ReturnType<typeof redisConfiguration>
export function isRedisDirty(state: RedisConfigurationState) {
  return (
    state.baseline.tags !== signature(parseRedisTags(state.draft.tags)) ||
    Boolean(state.draft.computeProfileId)
  )
}
export function reapplyRedisChanges(
  state: RedisConfigurationState,
  redis: RedisService,
): RedisConfigurationState {
  const next = redisConfiguration(redis)
  return {
    ...next,
    draft: {
      tags:
        state.baseline.tags !== signature(parseRedisTags(state.draft.tags))
          ? state.draft.tags
          : next.draft.tags,
      computeProfileId: state.draft.computeProfileId,
    },
  }
}
export function acceptedRedisChanges(state: RedisConfigurationState): RedisConfigurationState {
  return {
    ...state,
    baseline: { ...state.baseline, tags: signature(parseRedisTags(state.draft.tags)) },
    draft: { ...state.draft, computeProfileId: '' },
    accepted: true,
  }
}
