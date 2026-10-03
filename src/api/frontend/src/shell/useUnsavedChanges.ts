import { useEffect } from 'react'
import { useAppServices } from './AppContext'

export function useUnsavedChanges(dirty: boolean, label: string) {
  const { navigation: { registerUnsavedChanges } } = useAppServices()
  useEffect(() => {
    if (dirty) return registerUnsavedChanges(label)
  }, [dirty, label, registerUnsavedChanges])
}
