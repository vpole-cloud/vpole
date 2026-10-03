import type { AppModule } from '../../shell/contracts'
import { PostgresView } from './PostgresView'

export const postgresModule: AppModule = {
  id: 'postgres',
  views: [{ id: 'postgres', label: 'PostgreSQL', component: PostgresView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/postgresql', label: 'PostgreSQL', viewId: 'postgres' }],
  commands: [{ id: 'postgres.create', label: 'Create PostgreSQL database', viewId: 'postgres', surface: 'palette',
    available: ({ tenant }) => tenant.canManage,
    run: ({ navigation }) => navigation.openResource({ resourceTypeId: 'pole/postgresql', resourceId: 'create' }) }],
}
