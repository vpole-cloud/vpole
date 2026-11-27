// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { Query } from '@tanstack/react-query'
import type { QuerySession } from '../data/querySession'
interface Pending {
  query: Query
  dirty: boolean
  timer?: ReturnType<typeof setTimeout>
}
export class InvalidationScheduler {
  private pending = new Map<string, Pending>()
  private epoch = 0
  private session: QuerySession
  private visible: () => boolean
  constructor(session: QuerySession, visible: () => boolean = () => !document.hidden) {
    this.session = session
    this.visible = visible
  }
  cancel() {
    this.epoch++
    for (const value of this.pending.values()) clearTimeout(value.timer)
    this.pending.clear()
  }
  invalidate(predicate: (queryKey: readonly unknown[], meta: Query['meta']) => boolean) {
    if (!this.session.isActive()) return
    for (const query of this.session.client
      .getQueryCache()
      .getAll()
      .filter((query) => predicate(query.queryKey, query.meta))) {
      // Mark every matching cache entry stale, including inactive pages, without triggering a read.
      void this.session.client.invalidateQueries({
        queryKey: query.queryKey,
        exact: true,
        refetchType: 'none',
      })
      if (!query.isActive() || !this.visible()) continue
      const pending = this.pending.get(query.queryHash)
      if (pending) {
        pending.dirty = true
        continue
      }
      const value: Pending = { query, dirty: true }
      this.pending.set(query.queryHash, value)
      const epoch = this.epoch
      value.timer = setTimeout(() => {
        void this.flush(value, epoch)
      }, 250)
    }
  }
  private async flush(value: Pending, epoch: number) {
    const valid = () =>
      epoch === this.epoch &&
      this.session.isActive() &&
      this.visible() &&
      value.query.isActive() &&
      this.session.client.getQueryCache().get(value.query.queryHash) === value.query
    try {
      while (value.dirty && valid()) {
        value.dirty = false
        // A change during an earlier GET needs one read after that GET, rather than cancelling it.
        if (value.query.state.fetchStatus === 'fetching') {
          await this.session.client.refetchQueries(
            { queryKey: value.query.queryKey, exact: true, type: 'active' },
            { cancelRefetch: false },
          )
          value.dirty = false
          if (!valid()) break
        }
        await this.session.client.refetchQueries(
          { queryKey: value.query.queryKey, exact: true, type: 'active' },
          { cancelRefetch: false },
        )
      }
    } finally {
      if (this.pending.get(value.query.queryHash) === value)
        this.pending.delete(value.query.queryHash)
    }
  }
}
