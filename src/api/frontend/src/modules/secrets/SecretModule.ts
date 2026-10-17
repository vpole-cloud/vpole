import type { AppModule } from '../../shell/contracts'
import { lazy } from 'react'
const SecretsView = lazy(() =>
  import('./SecretsView').then((module) => ({ default: module.SecretsView })),
)

export const secretModule: AppModule = {
  id: 'secrets',
  views: [{ id: 'secrets', label: 'Secrets', component: SecretsView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/secret', label: 'Secret', viewId: 'secrets' }],
  commands: [
    {
      id: 'secrets.create',
      label: 'Create Secret',
      viewId: 'secrets',
      surface: 'palette',
      available: ({ tenant }) => tenant.canManage,
      run: ({ navigation }) =>
        navigation.openResource({ resourceTypeId: 'pole/secret', resourceId: 'create' }),
    },
  ],
}
