import type { ComponentType } from 'react'
import type { Tenant } from '../types'

export type Theme = 'light' | 'dark'

export interface ConsoleServices {
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
    goTo: (viewId: string) => void
    registerUnsavedChanges: (label: string) => () => void
  }
  theme: { value: Theme; set: (theme: Theme) => void }
  notifications: { publish: (message: string, tone?: 'info' | 'error') => void }
}

export interface ConsoleView {
  id: string
  label: string
  component: ComponentType
  access: 'public' | 'authenticated' | 'tenant'
}

export interface ConsoleResourceType {
  id: string
  label: string
  viewId: string
}

export interface ConsoleCommand {
  id: string
  label: string
  viewId: string
  available?: (context: Pick<ConsoleServices, 'auth' | 'tenant'>) => boolean
  run: (services: ConsoleServices) => void
}

export interface ConsoleModule {
  id: string
  views: readonly ConsoleView[]
  resourceTypes?: readonly ConsoleResourceType[]
  commands?: readonly ConsoleCommand[]
}

export interface ConsoleRegistry {
  views: readonly ConsoleView[]
  resourceTypes: readonly ConsoleResourceType[]
  commands: readonly ConsoleCommand[]
}
