// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { createAppRegistry } from '../shell/moduleRegistry'
import { tenantsModule } from './tenants/TenantsModule'
import { gitOpsModule } from './gitops/GitOpsModule'
import { backupsModule } from './backups/BackupsModule'
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
  tenantsModule,
  resourceGroupsModule,
  webServicesModule,
  postgresModule,
  redisModule,
  secretModule,
  domainsModule,
  integrationsModule,
  gitOpsModule,
  backupsModule,
  storageModule,
])
