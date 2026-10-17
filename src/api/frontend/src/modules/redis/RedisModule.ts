import type { AppModule } from '../../shell/contracts'
import { lazy } from 'react'
const RedisView = lazy(() =>
  import('./RedisView').then((module) => ({ default: module.RedisView })),
)

export const redisModule: AppModule = {
  id: 'redis',
  views: [{ id: 'redis', label: 'Redis', component: RedisView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/redis', label: 'Redis', viewId: 'redis' }],
  commands: [
    {
      id: 'redis.create',
      label: 'Create Redis service',
      viewId: 'redis',
      surface: 'palette',
      available: ({ tenant }) => tenant.canManage,
      run: ({ navigation }) =>
        navigation.openResource({ resourceTypeId: 'pole/redis', resourceId: 'create' }),
    },
  ],
}
