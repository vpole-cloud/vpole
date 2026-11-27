// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { lazy } from 'react'
import type { AppModule } from '../../shell/contracts'
const MembersView = lazy(() =>
  import('./MembersView').then((module) => ({ default: module.MembersView })),
)
export const tenantsModule: AppModule = {
  id: 'tenant-members',
  views: [{ id: 'members', label: 'Members', component: MembersView, access: 'tenant' }],
}
