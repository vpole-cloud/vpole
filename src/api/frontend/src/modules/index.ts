import { createAppRegistry } from '../shell/moduleRegistry'
import { domainsModule, integrationsModule, overviewModule, resourceGroupsModule, storageModule } from './platform/PlatformModules'
import { webServicesModule } from './web-services/WebServicesModule'
import { postgresModule } from './postgres/PostgresModule'
import { redisModule } from './redis/RedisModule'

// All modules are trusted, statically bundled contributions. This is the only composition root.
export const appRegistry = createAppRegistry([
  overviewModule, resourceGroupsModule, webServicesModule, postgresModule, redisModule, domainsModule, integrationsModule, storageModule,
])
