// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { createContext, useContext } from 'react'
import type { AppServices } from './contracts'

export const AppContext = createContext<AppServices | null>(null)

export function useAppServices() {
  const services = useContext(AppContext)
  if (!services) throw new Error('App views must be mounted inside the app shell.')
  return services
}
