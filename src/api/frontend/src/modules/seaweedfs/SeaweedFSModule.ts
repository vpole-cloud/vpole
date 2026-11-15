import type { AppModule } from '../../shell/contracts'
import { lazy } from 'react'
const SeaweedFSView = lazy(() =>
  import('./SeaweedFSView').then((module) => ({ default: module.SeaweedFSView })),
)

export const seaweedFSModule: AppModule = {
  id: 'seaweedfs',
  views: [{ id: 'seaweedfs', label: 'SeaweedFS', component: SeaweedFSView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/seaweedfs', label: 'SeaweedFS', viewId: 'seaweedfs' }],
  commands: [
    {
      id: 'seaweedfs.create',
      label: 'Create SeaweedFS instance',
      viewId: 'seaweedfs',
      surface: 'palette',
      available: ({ tenant }) => tenant.canManage,
      run: ({ navigation }) =>
        navigation.openResource({ resourceTypeId: 'pole/seaweedfs', resourceId: 'create' }),
    },
  ],
}
