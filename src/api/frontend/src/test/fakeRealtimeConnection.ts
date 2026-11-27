// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { vi } from 'vitest'
import type { RealtimeConnection } from '../realtime/connection'
import type {
  RealtimeChange,
  RealtimeClientLifecycleReport,
  RealtimeCoverage,
  RealtimeSubscription,
} from '../realtime/types'

export class FakeConnection implements RealtimeConnection {
  private requests: RealtimeSubscription[] = []
  changed: (change: RealtimeChange) => void = () => {}
  sessionRevoked: () => void = () => {}
  accessRevoked: (tenantId?: string) => void = () => {}
  reconnecting: () => void = () => {}
  reconnected: () => void = () => {}
  close: () => void = () => {}
  start = vi.fn(async () => {})
  stop = vi.fn(async () => {})
  subscribe = vi.fn(async (requests: RealtimeSubscription[]): Promise<RealtimeCoverage[]> => {
    this.requests = requests
    return requests.map((request, index) => ({
      ...request,
      subscriptionId: String(index),
      covered: true,
    }))
  })
  heartbeat = vi.fn(async (): Promise<RealtimeCoverage[]> =>
    this.requests.map((request, index) => ({
      ...request,
      subscriptionId: String(index),
      covered: true,
    })),
  )
  reportLifecycle = vi
    .fn<(report: RealtimeClientLifecycleReport) => Promise<boolean>>()
    .mockResolvedValue(true)
  unsubscribe = vi.fn(async () => {
    this.requests = []
  })
  onChanged(callback: typeof this.changed) {
    this.changed = callback
  }
  onSessionRevoked(callback: typeof this.sessionRevoked) {
    this.sessionRevoked = callback
  }
  onAccessRevoked(callback: typeof this.accessRevoked) {
    this.accessRevoked = callback
  }
  onReconnecting(callback: typeof this.reconnecting) {
    this.reconnecting = callback
  }
  onReconnected(callback: typeof this.reconnected) {
    this.reconnected = callback
  }
  onClose(callback: typeof this.close) {
    this.close = callback
  }
}
