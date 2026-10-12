import type { Query } from '@tanstack/react-query'
import { ApiError } from '../api/transport'
import { credentialExpiresAt, fingerprintCredential } from '../auth/credentialIdentity'
import type { QuerySession } from '../data/querySession'
import type { RealtimeConnection } from './connection'
import { InvalidationScheduler } from './invalidationScheduler'
import { matchesPilotQuery } from './pilotQueries'
import { acceptsChange, subscriptionKey, type RealtimeChange, type RealtimeCoverage, type RealtimeSubscription, type RealtimeStatus } from './types'

export type CoordinationMessage = { type: 'renewed' | 'session-revoked' | 'access-revoked'; tenantId?: string }
interface Dependencies {
  bootstrap: (signal: AbortSignal, credential?: string) => Promise<{ expiresAtUtc: string }>
  connection: () => RealtimeConnection
  revoke: () => Promise<void>
  broadcast?: (message: CoordinationMessage) => void
  userId?: string
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
  private credentialEpoch = 0
  private platformDenialEpoch = 0
  private deniedCredentialEpoch = -1
  private credentialFingerprint: Promise<string | undefined> = Promise.resolve(undefined)
  private credentialExpiry?: number
  private deniedFingerprints = new Map<string, number>()
  private pendingDenials = new Set<Promise<void>>()
  private restoreBarrierUntil = 0
  private platformAdminClaim = false
  private membershipEpoch = 0
  private searchEpochs = new Map<string, number>()
  private knownMemberships?: Set<string>
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
  updateCredential(credential?: string, platformAdminClaim = false) {
    // A claim loss can unmount every privileged interest before Subscribe has a
    // chance to report denial. Fence locally, recording the old admin identity.
    if (this.platformAdminClaim && !platformAdminClaim) this.revokePlatformAccess()
    if (credential !== this.credential) {
      this.credentialEpoch++
      this.credentialFingerprint = credential ? fingerprintCredential(credential) : Promise.resolve(undefined)
      this.credentialExpiry = credential ? credentialExpiresAt(credential) : undefined
      this.pruneDeniedFingerprints()
    }
    this.credential = credential; this.platformAdminClaim = platformAdminClaim
  }
  setBroadcast(broadcast?: Dependencies['broadcast']) { this.dependencies.broadcast = broadcast }
  private publish() { this.snapshot++; for (const listener of this.listeners) listener() }
  ownsApi(baseUrl: unknown) { return baseUrl === this.baseUrl }
  allowed(baseUrl: unknown, tenantId?: unknown) {
    return baseUrl !== this.baseUrl || !this.revoked && (!tenantId || !this.blockedTenants.has(String(tenantId)))
  }
  platformAllowed(baseUrl: unknown) { return baseUrl !== this.baseUrl || !this.revoked && !this.platformRevoked }
  searchEpoch(baseUrl: string, tenantId?: string) { return this.ownsApi(baseUrl) && tenantId ? this.searchEpochs.get(tenantId) ?? 0 : 0 }
  private advanceSearchEpoch(tenantId?: string) {
    if (tenantId) this.searchEpochs.set(tenantId, (this.searchEpochs.get(tenantId) ?? 0) + 1)
  }
  beginMembershipRead(baseUrl: string, userId: string) {
    if (!this.ownsApi(baseUrl) || !userId || userId !== this.dependencies.userId || this.revoked || !this.session.isActive()) throw new DOMException('Aborted', 'AbortError')
    return this.membershipEpoch
  }
  acceptMembershipSnapshot(baseUrl: string, userId: string, epoch: number, tenantIds: readonly string[], signal: AbortSignal) {
    if (!this.ownsApi(baseUrl) || signal.aborted || epoch !== this.membershipEpoch || this.revoked || !this.session.isActive() || userId !== this.dependencies.userId) return false
    const next = new Set(tenantIds)
    // Include inactive tenant cache owners, so a first authoritative read also removes old
    // membership evidence. User discovery and platform data have independent scopes.
    const previous = this.knownMemberships ?? new Set(this.session.client.getQueryCache().getAll()
      .filter(query => this.matchesScope(query.queryKey, query.queryKey[4] as string | undefined) &&
        typeof query.queryKey[4] === 'string' && query.meta?.authorizationScope !== 'user' && query.queryKey[5] !== 'tenants')
      .map(query => String(query.queryKey[4])))
    const removed = [...previous].filter(id => !next.has(id))
    const restored = [...this.blockedTenants].filter(id => next.has(id))
    this.knownMemberships = next
    for (const id of removed) { this.blockedTenants.add(id); this.clearTenant(id) }
    for (const id of restored) this.blockedTenants.delete(id)
    if (removed.length || restored.length) this.changedInterests()
    return true
  }
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
        const credential = this.credential; const credentialEpoch = this.credentialEpoch
        const denialEpoch = this.platformDenialEpoch; const adminClaim = this.platformAdminClaim
        const identity = this.credentialFingerprint; const expiresAt = this.credentialExpiry
        const session = await this.dependencies.bootstrap(abort.signal, credential)
        if (!valid()) return
        // Only this browser's fresh, current bearer bootstrap can restore denied authority.
        // Cookie reconnects, unchanged credentials and a response overtaken by denial cannot.
        if (this.platformRevoked && credential && adminClaim) {
          const [fingerprint] = await Promise.all([identity, Promise.all([...this.pendingDenials])])
          if (!valid()) return
          this.pruneDeniedFingerprints()
          if (fingerprint && this.platformAdminClaim && credentialEpoch === this.credentialEpoch &&
            denialEpoch === this.platformDenialEpoch && credentialEpoch > this.deniedCredentialEpoch &&
            (expiresAt === undefined || expiresAt > Date.now()) && Date.now() >= this.restoreBarrierUntil && !this.deniedFingerprints.has(fingerprint)) {
            this.platformRevoked = false
            this.changedInterests()
          }
        }
        const remaining = Date.parse(session.expiresAtUtc) - Date.now()
        // A cookie capped by the unchanged JWT cannot gain lifetime through another early
        // bootstrap. Wait for its actual expiry or a credential update in that final margin.
        this.expiry = setTimeout(() => { void this.renew() }, Math.max(1000, remaining > 10000 ? remaining - 10000 : remaining))
        this.dependencies.broadcast?.({ type: 'renewed' })
      }
      const connection = this.dependencies.connection(); this.connection = connection
      connection.onChanged(change => {
        if (!valid()) return
        // A user hint can precede Subscribe's acknowledgement. Fence the older HTTP read
        // immediately; the scheduled resync and acknowledgement still use authoritative GETs.
        if (change.topic === 'tenant-access' && change.resourceId === this.dependencies.userId &&
          [...this.interests.values()].some(({ request }) => acceptsChange(request, change))) {
          this.membershipEpoch++
          this.scheduler.invalidate((key, meta) => this.matchesChange(key, change, meta))
          return
        }
        if (this.status !== 'connected') return
        const accepted = [...this.interests.values()].some(({ request }) => this.covered(request) && acceptsChange(request, change))
        if (accepted) {
          if (change.topic === 'search') { this.advanceSearchEpoch(change.tenantId); this.publish() }
          this.scheduler.invalidate((key, meta) => this.matchesChange(key, change, meta))
        }
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
      .filter(request => this.allowed(this.baseUrl, request.tenantId) && (request.topic === 'tenant-access'
        ? request.tenantId === undefined && Boolean(request.resourceId) && request.resourceId === this.dependencies.userId
        : request.tenantId !== undefined || this.platformAllowed(this.baseUrl)))
      // Membership discovery must not be crowded out by a tree or retained resource tabs.
      .sort((left, right) => Number(right.topic === 'tenant-access') - Number(left.topic === 'tenant-access')).slice(0, 64)
  }
  private matchesChange(key: readonly unknown[], change: RealtimeChange, meta?: Record<string, unknown>) {
    if (change.topic === 'tenant-access') return Boolean(change.resourceId) && change.resourceId === this.dependencies.userId &&
      this.matchesScope(key, change.resourceId) && key[5] === 'tenants'
    return this.matchesScope(key, change.tenantId) && matchesPilotQuery(key.slice(5), change, meta)
  }
  private acknowledge(requests: RealtimeSubscription[], acknowledgements: RealtimeCoverage[], resyncAll: boolean) {
    const requested = new Set(requests.map(subscriptionKey))
    const next = new Set(acknowledgements.filter(value => value.covered && requested.has(subscriptionKey(value))).map(subscriptionKey))
    const resync = requests.filter(request => next.has(subscriptionKey(request)) && (resyncAll || !this.coverage.has(subscriptionKey(request))))
    const changed = next.size !== this.coverage.size || [...next].some(key => !this.coverage.has(key))
    this.coverage = next
    const searchResync = resync.filter(request => request.topic === 'search')
    for (const tenantId of new Set(searchResync.map(request => request.tenantId))) this.advanceSearchEpoch(tenantId)
    if (changed || searchResync.length) this.publish()
    if (resync.length) this.scheduler.invalidate((key, meta) => resync.some(request => this.matchesChange(key, { version: 1, ...request }, meta)))
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
    this.membershipEpoch++
    if (tenantId) this.blockedTenants.add(tenantId)
    else this.revoked = true
    this.scheduler.cancel(); this.coverage.clear(); this.publish()
    if (broadcast) this.dependencies.broadcast?.({ type: tenantId ? 'access-revoked' : 'session-revoked', tenantId })
    if (tenantId) { this.clearTenant(tenantId); this.refreshDiscovery(); this.changedInterests() }
    else {
      const predicate = (query: Query) => query.queryKey[3] === this.baseUrl
      void this.session.client.cancelQueries({ predicate }); this.session.client.removeQueries({ predicate })
      void this.disconnect()
    }
  }
  private revokePlatformAccess(broadcast = true) {
    this.membershipEpoch++
    this.platformDenialEpoch++; this.deniedCredentialEpoch = this.credentialEpoch
    if (this.credential) {
      const expiresAt = this.credentialExpiry ?? Infinity
      const pending = this.credentialFingerprint.then(fingerprint => {
        if (!fingerprint) return
        this.pruneDeniedFingerprints()
        if (expiresAt <= Date.now()) return
        this.deniedFingerprints.set(fingerprint, expiresAt)
        // Bound unknown/long-lived token histories too. An evicted unexpired identity creates
        // a recovery barrier until its horizon, so an ABA replay cannot exploit eviction.
        if (this.deniedFingerprints.size > 64) {
          const [oldest, horizon] = this.deniedFingerprints.entries().next().value!
          this.deniedFingerprints.delete(oldest)
          this.restoreBarrierUntil = Math.max(this.restoreBarrierUntil, horizon)
        }
      }).finally(() => this.pendingDenials.delete(pending))
      this.pendingDenials.add(pending)
    }
    this.platformRevoked = true
    this.changedInterests()
    if (broadcast) this.dependencies.broadcast?.({ type: 'access-revoked' })
    const privileged = (query: Query) =>
      query.queryKey[3] === this.baseUrl && query.queryKey[4] == null && query.meta?.privileged === true
    void this.session.client.cancelQueries({ predicate: privileged }); this.session.client.removeQueries({ predicate: privileged })
    this.refreshDiscovery()
  }
  private pruneDeniedFingerprints() {
    for (const [fingerprint, expiresAt] of this.deniedFingerprints) if (expiresAt <= Date.now()) this.deniedFingerprints.delete(fingerprint)
  }
  private refreshDiscovery() {
    const epoch = this.membershipEpoch
    const predicate = (query: Query) => query.queryKey[3] === this.baseUrl && query.queryKey[5] === 'tenants'
    void this.session.client.cancelQueries({ predicate }).then(() => {
      // Keep the last authoritative list while revalidating. Clearing it would unmount
      // ordinary tenant workspaces on platform-only denial; affected tenant origins are
      // already synchronously blocked and cleared by a tenant-specific denial.
      if (epoch === this.membershipEpoch && !this.revoked && this.session.isActive())
        void this.session.client.invalidateQueries({ predicate, refetchType: 'active' }, { cancelRefetch: false })
    })
  }
  private clearTenant(tenantId: string) {
    const predicate = (query: Query) => this.matchesScope(query.queryKey, tenantId) && query.meta?.authorizationScope !== 'user' && query.queryKey[5] !== 'tenants'
    void this.session.client.cancelQueries({ predicate })
    this.session.client.removeQueries({ predicate })
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
