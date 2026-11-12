import { lazy } from 'react'
import type { AppModule } from '../../shell/contracts'

const BackupsView = lazy(() =>
  import('./BackupsView').then((module) => ({ default: module.BackupsView })),
)

export const backupsModule: AppModule = {
  id: 'backups',
  views: [{ id: 'backups', label: 'Backups', component: BackupsView, access: 'tenant' }],
  resourceTypes: [
    { id: 'pole/backup-destination', label: 'Backup destination', viewId: 'backups' },
    { id: 'pole/backup-policy', label: 'Backup policy', viewId: 'backups' },
  ],
}
