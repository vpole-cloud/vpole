import { createAppRegistry } from '../shell/moduleRegistry'
import { gitOpsModule } from './gitops/GitOpsModule'
import {
  domainsModule,
  integrationsModule,
  overviewModule,
  resourceGroupsModule,
  storageModule,
} from './platform/PlatformModules'
import { webServicesModule } from './web-services/WebServicesModule'
import { postgresModule } from './postgres/PostgresModule'
import { redisModule } from './redis/RedisModule'
import { secretModule } from './secrets/SecretModule'

// All modules are trusted, statically bundled contributions. This is the only composition root.
export const appRegistry = createAppRegistry([
  overviewModule,
  resourceGroupsModule,
  webServicesModule,
  postgresModule,
  redisModule,
  secretModule,
  domainsModule,
  integrationsModule,
  gitOpsModule,
  storageModule,
])
