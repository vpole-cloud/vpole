// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useEffect } from 'react'
import { useAppServices } from './AppContext'

export function useUnsavedChanges(dirty: boolean, label: string) {
  const {
    navigation: { registerUnsavedChanges },
  } = useAppServices()
  useEffect(() => {
    if (dirty) return registerUnsavedChanges(label)
  }, [dirty, label, registerUnsavedChanges])
}
