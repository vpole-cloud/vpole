import type { ConsoleModule } from '../../console/contracts'
import { WebServicesView } from './WebServicesView'

export const webServicesModule: ConsoleModule = {
  id: 'web-services',
  views: [{ id: 'web-services', label: 'Web services', component: WebServicesView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/web-service', label: 'Web service', viewId: 'web-services' }],
}
