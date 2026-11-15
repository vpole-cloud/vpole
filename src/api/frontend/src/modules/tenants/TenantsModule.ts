import { lazy } from 'react'
import type { AppModule } from '../../shell/contracts'
const MembersView = lazy(() =>
  import('./MembersView').then((module) => ({ default: module.MembersView })),
)
export const tenantsModule: AppModule = {
  id: 'tenant-members',
  views: [{ id: 'members', label: 'Members', component: MembersView, access: 'tenant' }],
}
