// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { AppServices } from '../../shell/contracts'
export function gitOpsTestServices(overrides: Partial<AppServices> = {}): AppServices {
  return {
    auth: {
      initialized: true,
      authenticated: true,
      userId: 'user',
      userName: 'User',
      isPlatformAdmin: false,
      login: () => {},
      logout: () => {},
    },
    api: { baseUrl: 'https://api.test', tenantId: 'tenant', token: 'token' },
    tenant: {
      active: { id: 'tenant', name: 'Tenant', role: 'Owner', isDefault: true },
      canManage: true,
    },
    navigation: {
      viewId: 'gitops',
      search: '?view=gitops&resource=source&section=overview',
      setSearch: () => {},
      goTo: () => {},
      openResource: () => {},
      resourceHref: (value) => `/?view=gitops&resource=${value.resourceId}`,
      registerUnsavedChanges: () => () => {},
    },
    theme: { value: 'light', set: () => {} },
    notifications: { publish: () => {} },
    resourceTypes: [],
    ...overrides,
  }
}
