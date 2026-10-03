import { createContext, useContext } from 'react'
import type { ConsoleServices } from './contracts'

export const ConsoleContext = createContext<ConsoleServices | null>(null)

export function useConsoleServices() {
  const services = useContext(ConsoleContext)
  if (!services) throw new Error('Console views must be mounted inside the console shell.')
  return services
}
