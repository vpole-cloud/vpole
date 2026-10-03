import { createConsoleRegistry } from '../console/moduleRegistry'
import { domainsModule, integrationsModule, overviewModule, resourceGroupsModule, storageModule } from './platform/PlatformModules'
import { webServicesModule } from './web-services/WebServicesModule'

// All modules are trusted, statically bundled contributions. This is the only composition root.
export const consoleRegistry = createConsoleRegistry([
  overviewModule, resourceGroupsModule, webServicesModule, domainsModule, integrationsModule, storageModule,
])
