// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { Component, type ReactNode } from 'react'
import { Button, EmptyState } from '../ui/components'

export class ModuleViewBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }
  static getDerivedStateFromError() {
    return { failed: true }
  }
  render() {
    return this.state.failed ? (
      <EmptyState title="This view could not be loaded." error>
        <p>Reload to reconnect. You can also open another view.</p>
        <Button onClick={() => window.location.reload()}>Reload workspace</Button>
      </EmptyState>
    ) : (
      this.props.children
    )
  }
}
