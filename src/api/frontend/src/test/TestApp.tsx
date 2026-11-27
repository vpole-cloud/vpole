// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { createRoot } from 'react-dom/client'
import { SessionQueries } from '../data/SessionQueries'

export function createTestApp() {
  const container = document.createElement('div')
  document.body.append(container)
  const domRoot = createRoot(container)
  const root = {
    ...domRoot,
    render: (node: Parameters<typeof domRoot.render>[0]) =>
      domRoot.render(<SessionQueries identity="test">{node}</SessionQueries>),
    unmount: () => domRoot.unmount(),
  }
  return { container, root }
}
