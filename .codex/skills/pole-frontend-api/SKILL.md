---
name: pole-frontend-api
description: Workflow for Pole's Vite React frontend, feature API/DTO ownership, scoped query state, drafts, authentication and active-tenant behavior.
---

# Pole Frontend API

Preserve backend contracts, tenant isolation and the verified query/draft/workspace separation.

## Inspect ownership first

1. Read the relevant backend endpoint and feature-owned frontend API/types/model/hook.
2. Use `modules/web-services/useWebService.ts`, `WebServiceResourceView.tsx`, and `formModel.ts` as the representative detail/draft slice; PostgreSQL `configurationModel.ts` and its resource view are the explicit concurrent-edit example.
3. Inspect `data/useScopedQuery.ts`, `data/fetchScopedQuery.ts`, `data/useApiScope.ts`, `data/useProviderInvalidation.ts`, and `shell/useWorkspaceController.ts` before adding lifecycle or navigation mechanics.

## HTTP and contracts

- Feature modules own routes, DTOs and pure body/response transformations. `api.ts` and `types.ts` are thin compatibility exports; shared eligibility and stream HTTP adapters live under `src/api`.
- Only `api/transport.ts` calls fetch. Use its `buildHeaders`, `fetchJson`, `sendJson`, `request` and typed `ApiError { status, code, fieldErrors, message }`.
- Tenant APIs carry both bearer authentication and `X-Pole-Tenant-ID`. Tenant discovery carries no tenant header.
- Fetch the current token from `useApiScope.credential()` immediately before each request, including every pagination page. Never put tokens in query keys, JSX keys, URL or storage.
- Secrets, domains and logs use their safe error mappings. Never surface arbitrary response text for those endpoints.

## Three kinds of state

- Server metadata: TanStack Query v5 with session/API/tenant identity, complete endpoint parameters and feature-owned query keys. Use `data/queryKeys.ts` for shared eligibility.
- Forms: pure baseline/draft/revision models. A background read cannot replace dirty fields. An accepted save adopts current server state after its authoritative read; a conflict requires explicit review/reapply or discard.
- Workspace: `useWorkspaceController` for tabs, labels, URL/history, persistence and dirty close guards. Persist only references. Use explicit destination fragments; ordinary links clear the previous hash.

Use data presence to keep loaded content mounted during background loading/errors. Expose initial loading and background refresh separately. Retries are false; staleTime, gcTime, mount/reconnect/focus behavior and intervals are explicit. Poll only active visible screens and coalesce slow reads with `refresh()`; do not cancel them on each timer tick. `refresh()` returns null on failed current reads while retaining display data. `refreshAfterWrite()` cancels pre-write reads before fetching fresh state.

Metadata commands use `hasHealthyCurrentQueryData` to keep healthy current cached values actionable during background reads. Keep permission, scope, revision, phase and eligibility checks in the feature. Capture the selected command tuple for confirmations and recheck it before submitting; changed metadata or failed refresh requires review. This predicate does not prove native observation freshness.

Imperative metadata reads use `fetchScopedQuery`; never store a different data envelope under a shared query key. Its temporary active observer preserves coalesced reads when rendered consumers disable. Bind ownership to `useApiScope.onExit`; cancel only after the last active owner leaves.

Account/logout changes cancel and clear the session cache. Tenant/screen changes cancel reads and fence old callbacks. Permission changes separately disable actions and cancel/remove privileged metadata. Credential renewal preserves ordinary drafts and tabs. Keep `pole.activeTenantId` discovery-based and preserve page-number versus cursor pagination.

## Realtime and observation ownership

- Read `realtime/controller.ts`, `invalidationScheduler.ts` and the owning feature interest/query matcher before adding notifications. One account/API cookie connection serves independently scoped tenant subscriptions; ordinary HTTP keeps bearer headers. Never add an accessTokenFactory or a bearer token to a hub URL.
- TanStack Query remains authoritative. Hints only invalidate matching metadata and advance observation fences; a current server acknowledgement alone grants coverage. Coalesce bursts, keep a running GET alive and perform one trailing read. Reconnect/resubscribe and visibility restoration resync active queries; inactive queries only become stale.
- Explicitly declare every topic needed by a composite query. Covered metadata uses 60-second safety reads; uncovered screens retain their existing intervals or manual behavior. Runtime uses covered30s/uncovered20s; admin storage uses covered30s and its initial/manual/visibility fallback.
- Observation keys/read epochs include deployment revision/generation or target connection version, never catalog revision in place of credential version. Use controller observation read guards and retained version floors. Do not accept a late, mismatched or incomplete snapshot as current evidence.
- Health/storage evidence expires after45s. Use authoritative checked time and query acceptance time, with bounded timer/visibility updates; keep the last complete rows and dirty form mounted, but disable storage writes while evidence is stale. Permission loss clears privileged metadata/forms.
- Client lifecycle reports contain only bounded ephemeral reconnect/fallback counts; diagnostic errors cannot change authentication, subscriptions or query work. Logs remain bounded uncached HTTP streams. See `docs/realtime-hosting.md` and frontend README for rollback and precise fallback policies.

## Mutations and sensitive state

- Capture an immutable command and originating scope before the first write. Use uncached `useScopedMutation`; guard each continuation before UI effects, navigation or another write.
- Accepted writes, unknown outcomes and refresh failures are distinct. Retry reads after an accepted write; do not send the write again merely because its refresh failed.
- Use scoped targeted invalidation for inventory/detail and dependent group/Overview/Search/operations/health/endpoint/runtime metadata. `useProviderInvalidation` cancels stale reads first. Global refresh counters and custom metadata read managers are obsolete.
- Git creation uses the discriminated-union reducer in `creationWorkflow.ts`. Recover known accepted IDs by reading server state. An unknown initial create is inspect-only without authoritative idempotency/correlation; never blindly recreate or adopt a matching row.
- PostgreSQL update sends positive expectedRevision and only changed fields. Preserve persistence CAS/outbox atomicity and typed 409 conflict. Recipe and deployment APIs currently have no authoritative browser CAS; retain explicit observed-baseline review without claiming stronger protection.
- Passwords, Secret values, Git credentials, connection strings and log content stay out of query and mutation cache. Use short-lived local state and clear on the documented ownership/permission/visibility boundaries. Stream output remains bounded and uncached.

## Rendering and verification

- Keep the current operational UI and native accessible controls. Use `Field`, `Notice`, `DataTable`, `StatusBadge` and `ConfirmationDialog` for shared presentation.
- Resource/deployment rows use stable ID-based data-testid containers. Repeated status/panel IDs must be scoped to a visible or keyed container; controls use role/label locators. Update browser selectors in the same change.
- Test with shared DTO factories, TestApp/session QueryClient and strict deferred HTTP fixtures. Verify unknown requests even when UI catches failures.
- Run current `npm test`, `npm run build`, `npm run lint`. Add focused regressions for origin races, slow polling, dirty refresh/conflicts, accepted-write/read-failure and sensitive-cache boundaries.
- For auth lifecycle changes validate a real Keycloak/browser refresh flow. For API revisions update repository callers and API tests together. Publication requires the complete local quality gate and independent review per AGENTS.md.
