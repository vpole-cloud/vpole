import { useEffect } from 'react'
import { useConsoleServices } from './ConsoleContext'

export function useUnsavedChanges(dirty: boolean, label: string) {
  const { navigation: { registerUnsavedChanges } } = useConsoleServices()
  useEffect(() => {
    if (dirty) return registerUnsavedChanges(label)
  }, [dirty, label, registerUnsavedChanges])
}
