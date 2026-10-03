import type { AppModule } from '../../shell/contracts'
import { WebServicesView } from './WebServicesView'

export const webServicesModule: AppModule = {
  id: 'web-services',
  views: [{ id: 'web-services', label: 'Web services', component: WebServicesView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/web-service', label: 'Web service', viewId: 'web-services' }],
}
