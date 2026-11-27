// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import { AuthenticatedApp } from './auth/AuthenticatedApp'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthenticatedApp />
  </StrictMode>,
)
