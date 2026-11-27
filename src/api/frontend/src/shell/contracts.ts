// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { ComponentType } from 'react'
import type { Tenant } from '../types'

export type Theme = 'light' | 'dark'
export interface ResourceIdentity {
  resourceTypeId: string
  resourceId: string
  section?: string
  fragment?: string
}

export interface AppServices {
  auth: {
    initialized: boolean
    authenticated: boolean
    userId: string
    userName?: string
    isPlatformAdmin: boolean
    login: () => void
    logout: () => void
  }
  tenant: { active: Tenant | null; canManage: boolean }
  api: { baseUrl: string; token?: string; tenantId?: string }
  navigation: {
    viewId: string
    search: string
    setSearch: (params: URLSearchParams, options?: { replace?: boolean }) => void
    goTo: (viewId: string) => void
    openResource: (resource: ResourceIdentity) => void
    resourceHref: (resource: ResourceIdentity) => string
    registerUnsavedChanges: (label: string) => () => void
  }
  theme: { value: Theme; set: (theme: Theme) => void }
  notifications: { publish: (message: string, tone?: 'info' | 'error') => void }
  resourceTypes: readonly ResourceTypeRegistration[]
}

export interface AppView {
  id: string
  label: string
  component: ComponentType
  access: 'public' | 'authenticated' | 'tenant'
}

export interface ResourceTypeRegistration {
  id: string
  label: string
  viewId: string
}

export interface AppCommand {
  id: string
  label: string
  viewId: string
  surface?: 'palette' | 'both'
  available?: (
    context: Pick<AppServices, 'auth' | 'tenant' | 'resourceTypes'> & {
      location: Pick<AppServices['navigation'], 'viewId' | 'search'>
    },
  ) => boolean
  run: (services: AppServices) => void
}

export interface AppModule {
  id: string
  views: readonly AppView[]
  resourceTypes?: readonly ResourceTypeRegistration[]
  commands?: readonly AppCommand[]
  navigationPanel?: ComponentType
}

export interface AppRegistry {
  views: readonly AppView[]
  resourceTypes: readonly ResourceTypeRegistration[]
  commands: readonly AppCommand[]
  navigationPanels: readonly { id: string; component: ComponentType }[]
}
