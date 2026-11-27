// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { lazy } from 'react'
import type { AppModule } from '../../shell/contracts'
import { gitOpsSourceResourceType } from '../../data/gitOps'
const GitOpsView = lazy(() =>
  import('./GitOpsView').then((module) => ({ default: module.GitOpsView })),
)
export const gitOpsModule: AppModule = {
  id: 'gitops',
  views: [{ id: 'gitops', label: 'GitOps', component: GitOpsView, access: 'tenant' }],
  resourceTypes: [{ id: gitOpsSourceResourceType, label: 'GitOps source', viewId: 'gitops' }],
  commands: [
    {
      id: 'gitops.create',
      label: 'Create GitOps source',
      viewId: 'gitops',
      surface: 'palette',
      available: ({ tenant }) => tenant.canManage,
      run: ({ navigation }) =>
        navigation.openResource({ resourceTypeId: gitOpsSourceResourceType, resourceId: 'create' }),
    },
  ],
}
