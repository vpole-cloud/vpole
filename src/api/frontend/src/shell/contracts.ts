import type { ComponentType } from 'react'
import type { Tenant } from '../types'

export type Theme = 'light' | 'dark'

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
    registerUnsavedChanges: (label: string) => () => void
  }
  theme: { value: Theme; set: (theme: Theme) => void }
  notifications: { publish: (message: string, tone?: 'info' | 'error') => void }
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
  available?: (context: Pick<AppServices, 'auth' | 'tenant'>) => boolean
  run: (services: AppServices) => void
}

export interface AppModule {
  id: string
  views: readonly AppView[]
  resourceTypes?: readonly ResourceTypeRegistration[]
  commands?: readonly AppCommand[]
}

export interface AppRegistry {
  views: readonly AppView[]
  resourceTypes: readonly ResourceTypeRegistration[]
  commands: readonly AppCommand[]
}
