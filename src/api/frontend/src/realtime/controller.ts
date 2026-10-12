import type { Query } from '@tanstack/react-query'
import { ApiError } from '../api/transport'
import type { QuerySession } from '../data/querySession'
import type { RealtimeConnection } from './connection'
import { InvalidationScheduler } from './invalidationScheduler'
import { matchesPilotQuery } from './pilotQueries'
import { acceptsChange, subscriptionKey, type RealtimeCoverage, type RealtimeSubscription, type RealtimeStatus } from './types'

export type CoordinationMessage = { type: 'renewed' | 'session-revoked' | 'access-revoked'; tenantId?: string }
interface Dependencies {
  bootstrap: (signal: AbortSignal, credential?: string) => Promise<{ expiresAtUtc: string }>
  connection: () => RealtimeConnection
  revoke: () => Promise<void>
  broadcast?: (message: CoordinationMessage) => void
}

// Own one account connection independently of the workspace and its drafts.
export class RealtimeController {
  private listeners = new Set<() => void>()
  private interests = new Map<string, { request: RealtimeSubscription; owners: number }>()
  private coverage = new Set<string>()
  private blockedTenants = new Set<string>()
  private revoked = false
  private cookieMismatch = false
  private platformRevoked = false
  private epoch = 0
  private revision = 0
  private subscriptionEpoch = 0
  private snapshot = 0
  private connection?: RealtimeConnection
  private abort?: AbortController
  private retry?: ReturnType<typeof setTimeout>
  private expiry?: ReturnType<typeof setTimeout>
  private heartbeatTimer?: ReturnType<typeof setTimeout>
  private syncing?: { epoch: number; promise: Promise<void> }
  private scheduler: InvalidationScheduler
  private session: QuerySession
  private baseUrl: string
  private dependencies: Dependencies
  private credential?: string
  status: RealtimeStatus = 'offline'

  constructor(session: QuerySession, baseUrl: string, dependencies: Dependencies) {
    this.session = session; this.baseUrl = baseUrl; this.dependencies = dependencies
    this.scheduler = new InvalidationScheduler(session)
  }
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener) } }
  getSnapshot = () => this.snapshot
  updateCredential(credential?: string) { this.credential = credential }
  setBroadcast(broadcast?: Dependencies['broadcast']) { this.dependencies.broadcast = broadcast }
  private publish() { this.snapshot++; for (const listener of this.listeners) listener() }
  allowed(baseUrl: unknown, tenantId?: unknown) {
    return baseUrl !== this.baseUrl || !this.revoked && (!tenantId || !this.blockedTenants.has(String(tenantId)))
  }
  platformAllowed(baseUrl: unknown) { return baseUrl !== this.baseUrl || !this.revoked && !this.platformRevoked }
  covered(request: RealtimeSubscription) { return this.status === 'connected' && this.coverage.has(subscriptionKey(request)) }
  register(request: RealtimeSubscription) {
    const key = subscriptionKey(request)
    const interest = this.interests.get(key)
    if (interest) interest.owners++
    else { this.interests.set(key, { request, owners: 1 }); this.changedInterests() }
    return () => {
      const current = this.interests.get(key)
      if (current && --current.owners === 0) { this.interests.delete(key); this.changedInterests() }
    }
  }
  private changedInterests() {
    this.revision++; this.scheduler.cancel(); this.coverage.clear(); this.publish()
    queueMicrotask(() => { void this.sync() })
  }
  private disconnect() {
    this.epoch++; this.subscriptionEpoch++; this.abort?.abort(); clearTimeout(this.retry); clearTimeout(this.expiry); clearTimeout(this.heartbeatTimer)
    this.scheduler.cancel(); this.coverage.clear(); this.status = 'offline'; this.publish()
    const old = this.connection; this.connection = undefined
    return old?.stop().catch(() => {}) ?? Promise.resolve()
  }
  async renew(bootstrap = true) {
    if (this.revoked || this.cookieMismatch || !this.session.isActive()) return
    const stopped = this.disconnect()
    const epoch = this.epoch
    const valid = () => epoch === this.epoch && !this.revoked && this.session.isActive()
    const abort = new AbortController(); this.abort = abort
    this.status = 'connecting'; this.publish()
    try {
      await stopped
      if (!valid()) return
      if (bootstrap) {
        const session = await this.dependencies.bootstrap(abort.signal, this.credential)
        if (!valid()) return
        const remaining = Date.parse(session.expiresAtUtc) - Date.now()
        // A cookie capped by the unchanged JWT cannot gain lifetime through another early
        // bootstrap. Wait for its actual expiry or a credential update in that final margin.
        this.expiry = setTimeout(() => { void this.renew() }, Math.max(1000, remaining > 10000 ? remaining - 10000 : remaining))
        this.dependencies.broadcast?.({ type: 'renewed' })
      }
      const connection = this.dependencies.connection(); this.connection = connection
      connection.onChanged(change => {
        if (!valid() || this.status !== 'connected') return
        const accepted = [...this.interests.values()].some(({ request }) => this.covered(request) && acceptsChange(request, change))
        if (accepted) this.scheduler.invalidate(key => this.matchesScope(key, change.tenantId) && matchesPilotQuery(key.slice(5), change))
      })
      connection.onSessionRevoked(() => { if (valid()) this.revokeAccess() })
      connection.onAccessRevoked(tenantId => {
        if (!valid()) return
        if (tenantId) this.revokeAccess(tenantId)
        else this.revokePlatformAccess()
      })
      connection.onReconnecting(() => {
        if (valid()) { this.subscriptionEpoch++; clearTimeout(this.heartbeatTimer); this.scheduler.cancel(); this.coverage.clear(); this.status = 'connecting'; this.publish() }
      })
      connection.onReconnected(() => { if (valid()) { this.status = 'connected'; this.publish(); this.scheduleHeartbeat(); void this.sync() } })
      connection.onClose(() => {
        if (!valid()) return
        clearTimeout(this.heartbeatTimer); this.scheduler.cancel(); this.coverage.clear(); this.status = 'offline'; this.publish()
        this.retry = setTimeout(() => { void this.renew() }, 10000)
      })
      await connection.start()
      if (!valid()) { await connection.stop(); return }
      this.status = 'connected'; this.publish(); this.scheduleHeartbeat(); await this.sync()
    } catch (failure) {
      if (!valid()) return
      const stopped = this.disconnect(); const retryEpoch = this.epoch
      await stopped
      if (retryEpoch !== this.epoch || this.revoked || !this.session.isActive()) return
      if (failure instanceof ApiError && failure.status === 409) this.cookieMismatch = true
      // Disabled servers and expired bearer credentials wait for the next credential change.
      if (!(failure instanceof ApiError && [401, 403, 404, 409].includes(failure.status)))
        this.retry = setTimeout(() => { if (retryEpoch === this.epoch) void this.renew() }, 10000)
    }
  }
  private matchesScope(key: readonly unknown[], tenantId?: string) {
    return key[0] === 'pole' && key[1] === this.session.identity && key[2] === this.session.id && key[3] === this.baseUrl && key[4] === tenantId
  }
  private requests() {
    return [...this.interests.values()].map(value => value.request)
      .filter(request => this.allowed(this.baseUrl, request.tenantId) && (request.tenantId !== undefined || this.platformAllowed(this.baseUrl))).slice(0, 64)
  }
  private acknowledge(requests: RealtimeSubscription[], acknowledgements: RealtimeCoverage[], resyncAll: boolean) {
    const requested = new Set(requests.map(subscriptionKey))
    const next = new Set(acknowledgements.filter(value => value.covered && requested.has(subscriptionKey(value))).map(subscriptionKey))
    const resync = requests.filter(request => next.has(subscriptionKey(request)) && (resyncAll || !this.coverage.has(subscriptionKey(request))))
    const changed = next.size !== this.coverage.size || [...next].some(key => !this.coverage.has(key))
    this.coverage = next
    if (changed) this.publish()
    if (resync.length) this.scheduler.invalidate(key => this.matchesScope(key, key[4] as string | undefined) && resync.some(request =>
      request.tenantId === key[4] && matchesPilotQuery(key.slice(5), { version: 1, ...request })))
  }
  private scheduleHeartbeat() {
    clearTimeout(this.heartbeatTimer)
    const connection = this.connection; const epoch = this.epoch
    if (!connection || this.status !== 'connected') return
    this.heartbeatTimer = setTimeout(() => { void this.checkHeartbeat(connection, epoch) }, 10000)
  }
  private async checkHeartbeat(connection: RealtimeConnection, epoch: number) {
    const subscriptionEpoch = this.subscriptionEpoch; const revision = this.revision
    const connected = () => this.epoch === epoch && this.connection === connection && this.status === 'connected' && this.session.isActive()
    const current = () => connected() && this.subscriptionEpoch === subscriptionEpoch && this.revision === revision
    if (!connected()) return
    try {
      const acknowledgements = await connection.heartbeat()
      if (current()) this.acknowledge(this.requests(), acknowledgements, false)
    } catch {
      // A live WebSocket can outlast Redis. Clear acknowledged coverage and reconnect through
      // ordinary bootstrap without treating a transient infrastructure failure as revocation.
      if (current()) { await this.renew(); return }
    }
    if (connected()) this.scheduleHeartbeat()
  }
  private sync() {
    const connection = this.connection; const epoch = this.subscriptionEpoch
    if (this.syncing?.epoch === epoch) return this.syncing.promise
    if (!connection || this.status !== 'connected') return Promise.resolve()
    let revision = this.revision
    const run = async () => {
      do {
        revision = this.revision
        const requests = this.requests()
        const acknowledgements = requests.length ? await connection.subscribe(requests) : (await connection.unsubscribe(), [])
        if (epoch !== this.subscriptionEpoch || this.status !== 'connected') return
        if (revision !== this.revision) continue
        // An acknowledgement closes the connect/subscribe gap; HTTP remains authoritative.
        this.acknowledge(requests, acknowledgements, true)
      } while (revision !== this.revision)
    }
    const promise = run().catch(() => {
      if (epoch === this.subscriptionEpoch) { this.coverage.clear(); this.publish() }
    }).finally(() => {
      if (this.syncing?.promise === promise) {
        this.syncing = undefined
        if (epoch === this.subscriptionEpoch && this.status === 'connected' && revision !== this.revision) void this.sync()
      }
    })
    this.syncing = { epoch, promise }
    return promise
  }
  revokeAccess(tenantId?: string, broadcast = true) {
    if (tenantId) this.blockedTenants.add(tenantId)
    else this.revoked = true
    this.scheduler.cancel(); this.coverage.clear(); this.publish()
    if (broadcast) this.dependencies.broadcast?.({ type: tenantId ? 'access-revoked' : 'session-revoked', tenantId })
    const affected = (key: readonly unknown[]) => key[3] === this.baseUrl && (!tenantId || key[4] === tenantId || key[5] === 'tenants')
    void this.session.client.cancelQueries({ predicate: query => affected(query.queryKey) }).then(() => {
      this.session.client.removeQueries({ predicate: query => affected(query.queryKey) && (!tenantId || query.queryKey[5] !== 'tenants') })
      if (tenantId) void this.session.client.resetQueries({ predicate: query => query.queryKey[3] === this.baseUrl && query.queryKey[5] === 'tenants' })
    })
    if (tenantId) this.changedInterests()
    else void this.disconnect()
  }
  private revokePlatformAccess(broadcast = true) {
    this.platformRevoked = true
    this.changedInterests()
    if (broadcast) this.dependencies.broadcast?.({ type: 'access-revoked' })
    const discovery = (key: readonly unknown[]) => key[3] === this.baseUrl && key[5] === 'tenants'
    const privileged = (query: Query) =>
      query.queryKey[3] === this.baseUrl && query.queryKey[4] == null && query.meta?.privileged === true
    void this.session.client.cancelQueries({ predicate: query => privileged(query) || discovery(query.queryKey) }).then(() => {
      this.session.client.removeQueries({ predicate: privileged })
      void this.session.client.resetQueries({ predicate: query => discovery(query.queryKey) })
    })
  }
  receive(message: CoordinationMessage) {
    if (message.type === 'renewed') { void this.renew(false); return }
    if (message.type === 'access-revoked' && !message.tenantId) this.revokePlatformAccess(false)
    else this.revokeAccess(message.type === 'access-revoked' ? message.tenantId : undefined, false)
  }
  async logout() {
    this.revokeAccess()
    try { await this.dependencies.revoke() } catch { /* IdP logout must still complete when the API is offline. */ }
  }
  dispose() { void this.disconnect() }
}
