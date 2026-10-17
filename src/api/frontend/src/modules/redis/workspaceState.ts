export const redisSections = ['overview', 'operations', 'configuration'] as const
export type RedisSection = (typeof redisSections)[number]
export const redisType = 'pole/redis'
export const parseRedisTags = (value: string) => [
  ...new Set(
    value
      .split(',')
      .map((tag) => tag.trim())
      .filter(Boolean),
  ),
]
