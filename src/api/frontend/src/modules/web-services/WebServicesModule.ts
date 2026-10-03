import type { ConsoleModule } from '../../console/contracts'
import { WebServicesView } from './WebServicesView'

export const webServicesModule: ConsoleModule = {
  id: 'web-services',
  views: [{ id: 'web-services', label: 'Web services', component: WebServicesView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/web-service', label: 'Web service', viewId: 'web-services' }],
  commands: [{
    id: 'web-services.open-groups', label: 'Resource groups', viewId: 'web-services',
    run: ({ navigation }) => navigation.goTo('resource-groups'),
  }],
}
