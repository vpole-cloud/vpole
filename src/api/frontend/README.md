# Pole web UI

The Vite/React UI talks to Pole's authenticated API. Its static module boundary is
in `src/shell/contracts.ts`. `src/modules/index.ts` explicitly composes trusted bundled modules;
the generic shell does not import resource providers.

## Module ownership

Owner views load lazily behind the shell's scoped Suspense/error boundary. Navigation and resource
registrations remain static; a module download resolves against the current tenant and a failed
download leaves other views usable. Reload reconnects a failed download.

Platform Storage requires platform administration for both reads and writes. A named deployment
target selects storage-class/PV/PVC inventory. Profile edits preserve other target mappings and
send the saved profile revision; changing target, profile or view protects unsaved drafts.

Domains uses separate zone catalog, reservation and ownership tabs. Zone details link exact
hostname owners and resource groups and show persisted target apply state. WebService Domains
owns per-target attach, change and confirmed detach, with generated hostname protection, public
ingress prerequisites, scoped cancellation and safe conflict messages. Replacement uses two
existing durable API writes; failed or unconfirmed detach preserves recovery through refreshed
ownership. Applied ingress configuration is distinct from external DNS, TLS and reachability.

WebService Dependencies separates network declarations, optional Secret authorization and explicit
environment references into independent steps. Saved connections and bindings survive reload;
partial target failures remain visible alongside successful targets. Provider endpoint metadata
supplies choices without a database-specific switch. Connection fields resolve separately on each
target; Secret keys use server-owned native references. Configuration requires confirmation and
the draft's expected revision. Conflicts preserve the draft and require review before adopting
a newer revision. Disconnect and detach confirmations identify their effects on shared resources.
See [WebService dependencies](../../features/WebServices/README.md#application-dependencies).

Redis owns a paginated/searchable catalog and URL-backed create, overview and configuration tabs.
Its single-target forms use backend-eligible storage/compute profiles and send `spec.storage` and
tags. Updates preserve the existing compute snapshot unless a replacement profile is selected.
Deletion is confirmed, deleting resources are read-only, and failed reconciliation can be retried.
Native StatefulSet storage size is fixed after creation; Redis updates only compute and tags.
`Applied` describes reconciliation; application connectivity is verified separately. Resource
catalog links on Overview are rendered from module registrations, including PostgreSQL and Redis.

Modules contribute view registrations, stable resource type identities, contextual commands and
optional navigation panels. ResourceGroups owns the paginated group/resource tree; the shell renders
registered panels without importing providers.
`createAppRegistry` rejects duplicate module/view/resource/command IDs and missing view
references. Providers own React views, hooks, DTOs and resource behavior. Registration metadata
stays in `.ts` files and components in `.tsx` files so React Fast Refresh works.

The shell exposes explicit auth, active tenant, API connection, navigation, theme and notification
services through `useAppServices`. Feature modules own API functions and DTOs; `src/api.ts` and `src/types.ts` are thin compatibility exports. Only `src/api/transport.ts` calls fetch; tenant
requests use bearer authentication and `X-Pole-Tenant-ID`. View access and disabled actions guide
the user; the server remains authoritative for authorization.

Views remount on user/tenant changes. Cleanup aborts their scoped reads and prevents previous-view
state from entering the new workspace. A pending server mutation continues after its view closes;
completion notifications from a previous tenant are ignored. Neither tokens nor form/secret values
are persisted by the shell. Theme preference is stored in `pole.console.theme`; tenant selection
retains `pole.activeTenantId`.

The header has one tenant selector with the membership role and default-tenant indicator, plus theme and login/logout actions.
The Members view manages invitations and membership with tenant Owner/Admin authority. A signed-in account with no memberships sees “No tenant access”; tenant workspace views stay hidden while account and platform administration remain available.
See [tenant invitations](../../features/Tenants/README.md) for the explicit onboarding and acceptance flow.
The shell does not repeat the selected tenant or account name in a separate session card.

Modules can register dirty forms with the shell's navigation service. Leaving a module, selecting
another tenant or logging out asks for confirmation; browser unload uses the native warning.
Browser Back/Forward uses a native confirmation when leaving a dirty view. Cancelling keeps the
current view and restores the history position, preserving Forward entries. Resource/section/page
navigation retains mounted drafts without asking for abandonment confirmation.

The generic navigation service exposes the current query and `setSearch` to registered providers;
providers own resource and section decoding. URLs include `tenant`, resolved only against the
current user's tenant discovery results before a view can issue requests. Unavailable tenants and
unregistered views have explicit states. Switching tenant clears the previous provider's URL
selection and remounts the workspace. The active tenant preference is synchronized only after
the URL tenant is confirmed accessible.

## Query, draft and mutation lifetimes

Server metadata uses TanStack Query v5 through `src/data/useScopedQuery.ts`. Each authenticated
session owns an isolated QueryClient. Keys include API URL, session identity, tenant and every
resource/filter parameter. Credentials are acquired from `useApiScope.credential()` immediately
before HTTP, including each pagination request. Tokens do not belong in query or React keys.
Logout/account changes cancel and clear the session cache. Tenant changes fence callbacks and
subsequent writes against their captured origin; permission changes separately disable actions
and clear privileged data.

Defaults are explicit: retry false, staleTime zero, five-minute inactive retention, mount/reconnect
refetch enabled and window-focus refetch disabled. Visible polling keeps each existing interval
and coalesces with a running read. Ordinary `refresh()` preserves the last data on failure and
returns null when no current successful result exists. `refreshAfterWrite()` cancels a pre-write
read before requesting authoritative state. Render loaded content by data presence; use
`isInitialLoading` and `isRefreshing` to distinguish initial loading from background work.
See [TanStack defaults](https://tanstack.com/query/latest/docs/framework/react/guides/important-defaults).

Metadata commands use `hasHealthyCurrentQueryData` to distinguish a healthy current cached result
from an initial or failed read. Background fetching alone does not disable a valid command.
Each feature still checks permissions, scope, revision, phase and eligibility. Confirmations capture
the selected command's identity and revision; a changed selection or failed refresh requires review.
The predicate does not establish native observation freshness.

Imperative metadata reads use `fetchScopedQuery` to share the same cache envelope and pending
request as rendered queries. A temporary observer owns the read until completion or scope exit;
exiting its last active owner cancels it without interrupting other active consumers.

Feature hooks own queries and mutations, `useWorkspaceController` owns tabs/history/persistence,
and form models own baseline/draft/revision. Background reads do not replace dirty fields.
WebService configuration and PostgreSQL updates send the draft's expected revision. PostgreSQL
sends only changed fields and requires explicit discard or review/reapply against a newer revision.
An accepted write whose refresh fails is not a failed write: retain the accepted state and retry
its read before permitting another save.

Writes use uncached `useScopedMutation` with an immutable submitted command and origin guards
before each continuation. Git creation uses `creationWorkflow.ts`; accepted service/config/job
IDs survive recoverable failures. An unknown initial create is inspect-only because the API has
no authoritative idempotency/correlation contract; it cannot safely repeat or adopt a matching
resource automatically. Recipe updates explicitly review an observed baseline; deployment and
recipe APIs do not currently expose browser expectedRevision fencing.

Query invalidation replaces global refresh counters. A provider owns its dependent namespaces
(inventory/detail, group resources, Overview/Search, operations/readiness/endpoints, and related
metadata). `useProviderInvalidation` cancels reads that began before accepted writes. Complete
eligibility keys are centralized in `src/data/queryKeys.ts`. Group choices load all server pages;
resource catalogs retain their page-number or cursor contract.

Passwords, Secret values, Git credentials, connection strings and log output never enter query
or mutation caches, URLs or browser storage. Write-only values and bounded log buffers stay in
local state and clear on their ownership/visibility/permission boundaries. Runtime NDJSON parsing
limits individual frames, unfinished tails and aggregate data independently of network chunks.

E2E uses `resource-row-<id>`, `deployment-row-<id>`, `operation-row-<id>`, `health-target-<id>`,
`build-row-<id>` and `release-row-<id>` containers. Scope repeated status IDs to their container or
visible panel. Buttons/fields/dialogs use role or label locators. Lint rejects incomplete union
switches, floating promises, concrete module imports in shared data adapters, and raw fetch outside
transport. Test fixtures use `strictFetch` and verify unexpected requests after rendering.

## Resource workspaces

PostgreSQL contributes its own catalog/create/resource workspace (`pole/postgresql`) and scoped
inventory, connection-string and password-rotation requests. CloudNativePG provides the workload.
The owner receives the administrative connection to the `postgres` database as `postgres` and
manages application databases and roles. Name, version, group, target and storage profile are fixed
after creation. Operator-managed storage can grow; updates preserve the resolved compute profile
unless the user selects a replacement. Readiness comes from the current deployment generation.

PostgreSQL workspace persistence saves only identities and sections under
`pole.postgres.workspace.v1.<user-id>.<tenant-id>`. Initial passwords clear when creation
becomes inactive. Revealed connection strings and rotation passwords clear on section/resource,
tenant/role changes or browser blur. Credential reads use `no-store`, require write access and
wait for the current deployment generation to be applied. Values never enter URLs or storage.
The screenshot suite captures credential sections before any value is entered or revealed.

WebService has an independent catalog, one creation tab and multiple open resource tabs. Each
resource offers Overview, Configuration, Dependencies, Deployments, Domains, Builds, Releases
and Operations.
Visited sections remain mounted so drafts survive switching sections and resources. Closing a
dirty resource or creation tab asks for confirmation; successful saves clear the dirty marker.
Source configuration and placement/alias drafts participate in the same guard. Viewer access
remains read-only. A late creation completion refreshes the catalog without reopening a closed tab.

Successful image creation opens the resource's Overview and reports configuration acceptance.
`Applied` reports reconciliation, not observed application readiness. Restored resources are read
directly by ID, independently of the first catalog page; missing/unavailable resources show an
explicit retry state. All other overview, resource-group, domain, Git integration and storage
screens remain registered.

Resource groups have an independent paginated catalog, creation tab and multiple open group tabs,
with Resources and Configuration sections. Metadata drafts use the same close/navigation guards
as service drafts. Group details are read directly by ID through the tenant-scoped API.
Group search matches names across all pages. Group resources paginate separately; their
`resourcePage` and `resourcePageSize` URL parameters preserve history and reload position.

Workspace references are stored under `pole.web-services.workspace.v1.<user-id>.<tenant-id>` and
`pole.resource-groups.workspace.v1.<user-id>.<tenant-id>`.
Only resource IDs, selected sections and the active resource/catalog tab are saved (up to 50
restored resources). Creation drafts, form contents, secrets and tokens stay in memory. Corrupt
or unavailable browser storage falls back to the catalog. Reload after confirming draft loss
loads the current server configuration.

WebService links use `?view=web-services&tenant=<id>&resource=<id>&section=configuration`.
`resource=catalog` selects the inventory; `resource=create` opens a fresh creation form after
reload. An explicit URL wins over stored active references and opens one tab per resource. A
legacy view-only link restores the stored workspace and receives a canonical URL without adding
a history entry. Resource IDs and section preferences remain references, never authorization.

The catalog reads every requested page from the tenant API. `page`, `pageSize` (1–100, default 8)
and `search` survive reload/history. Search applies server-side to service name or exact service
ID across all pages, rather than filtering the currently loaded page. Applying search or changing
page size starts at page 1. Superseded reads are aborted and late responses ignored. Refresh/page
errors retain the last loaded results with an explicit error and retry; unavailable pages clamp
to the last page. Drafts remain mounted while catalog parameters change.

Creation uses named active isolation scopes from the selected resource group's tenant API.
`Group default` delegates default-scope resolution to the backend. Selecting another scope sends
its opaque ID; changing groups clears the previous selection and aborts its pending read. Scope
IDs are not entered manually. Default creation stays usable if additional scopes cannot load;
the error and retry remain visible. Resource groups remain in primary navigation and the creation
form, rather than an unrelated shortcut above the WebService workspace.

Group links use `?view=resource-groups&tenant=<id>&resource=<id>&section=resources`.
Generic links add `resourceType=pole%2Fweb-service` or another registered type. The shell resolves
the provider from that type, including when a stored view hint is stale. The group catalog, group
contents and tree use real links that support copying and opening a new browser tab. Reopening
an identity selects its existing workspace tab. Unregistered resource types show an explicit
unavailable-provider state without guessing resource endpoints.

The tree stores only expanded group IDs and its page/open preference under
`pole.resource-tree.v1.<user-id>.<tenant-id>`. Restored IDs are matched against authorized loaded
groups before child requests; collapsed or superseded reads are aborted. Mutation acceptance
refreshes the tree. Group choices in service forms load every page, rather than just the first six
groups. Shared inventory hooks preserve loaded rows on failures and fence tenant/page responses;
the API client parses problem details and forms display field validation while retaining input.

This completes #12 for the currently registered resource types: ResourceGroup and WebService.
Adding a new UI provider still requires its own registration and resource workspace.
The #90 creation workspace now offers container-image and Git paths. Git selection can use an
existing linked source or select an accessible GitHub account, paginated repository/branch lists,
analyzed commit and build plan without leaving the service workflow. A missing connection links
to Integrations. The current WebService contract requires an initial image; the form explains
that it runs until the first source-built image is applied. Source linking, service creation,
configuration and queueing are separate durable operations. Failed setup retains the created
service and retries recover accepted configuration/job IDs before sending further writes.

Builds supports source configuration, latest-commit analysis, recipe inspection/editing, queueing,
history, failure codes and linked deployment results. Recipe drafts stay in memory and remain
guarded across refresh, tab/navigation and close. Completed jobs keep their immutable inputs;
editing a recipe affects future builds. Eligible profile loading/empty/error states block invalid
creation; a sole eligible profile or the target default supplies the ordinary path, with explicit
multi-target controls still available. Reconciliation is refreshed while pending; it is separate
from build completion and observed application readiness.
The recipe editor changes the executable Dockerfile and manifest path. The source directory,
detected build commands and start command are reference metadata; command changes belong in the
Dockerfile that the existing executor uses.

#6 adds the header Search control and Ctrl/⌘+K palette in every authenticated tenant workspace.
The dialog supports arrow/Enter/Escape navigation, focus restoration, paginated tenant-scoped
Search API reads and provider-registered commands. Resource results use the server's stable
`resourceTypeId` and the same module registry as tree links. A missing UI provider is explicit;
no endpoint is guessed. Query, auth or tenant changes cancel old reads; late results are ignored.
Existing projections cover groups, WebServices, PostgreSQL and Redis; domain/DNS resources will
appear when their backend projections exist. No separate index or cross-tenant search is added.

This does not complete #96's web UI release. GitOps
ownership and source management belong to #88; runtime diagnostics and readiness belong to #8/#95.

The `src/shell` directory owns AppShell, AppContext, module contracts and navigation guards.
Shared components in `src/ui/components.tsx` cover buttons, fields, tables, status, notices,
empty/error states and accessible confirmation dialogs. Semantic tokens in `src/index.css` provide
light/dark themes, with responsive layout in `src/App.css`.

Use `Field` for a native control with a hint: it names the control from the label alone and links
the hint as a description while preserving existing validation descriptions. Hand-written fields
need the same separation; including hints in a wrapping label changes its accessible name.

## Verification

The authenticated application owns a cookie-authenticated SignalR connection under `src/realtime`.
The pilot covers source-build configuration/jobs, WebService inventory/details/releases, and
WebService or unfiltered durable Operations queries. HTTP reads still own cached metadata; change
messages contain identities and versions rather than DTOs or log output. Subscription replacement
is acknowledged before active queries resync. Bursts coalesce for 250 ms, preserve ongoing reads,
and request one trailing read when a notification arrives during a GET. Inactive cached pages are
marked stale; hidden pages release their interests and do not poll pilot metadata.

Confirmed coverage uses a 60-second safety read. Disabled, disconnected or uncovered topics retain
their previous polling intervals. `VITE_REALTIME_ENABLED=false` disables browser realtime; the API
also requires `Realtime:Enabled`. Cookie bootstrap sends the current bearer through shared HTTP
transport without a tenant header. The hub uses cookies with credentials enabled, and browser
logout deletes that session before IdP logout. Tokens never enter hub URLs or notification data.
Credential renewal reconnects without replacing workspace tabs or drafts. Revocation cancels and
clears affected metadata and revalidates tenant discovery; credential-free BroadcastChannel messages
coordinate renewal and revocation among tabs for the same API/account. A cookie owned by another
account leaves realtime offline with bearer HTTP fallback until that browser session is resolved.
Production requires the same-site reverse proxy described by the Realtime feature; local HTTP
development must use the same hostname for UI and API so its Lax cookie is available.

Optional client lifecycle diagnostics aggregate only reconnect/fallback counts in the controller
for its current QuerySession and API. Attempts are singleflight and at least ten seconds apart;
false or failed reports retain bounded counts for a later healthy heartbeat. Rotation fences held
callbacks, and logout/dispose drops pending counts. Diagnostic errors never alter authorization,
coverage or ordinary actions. Reports contain no identities or tokens and never enter query caches,
browser storage or URLs. Reporting is best-effort; retry after an uncertain delivery can duplicate
a diagnostic count.

Run `npm test`, `npm run build` and `npm run lint` from this directory. Tests include independent
module composition, registration conflicts, tenant races, image mutation/errors, separate drafts,
dirty-close/navigation/unload guards, restoration, missing resources, URL precedence/history,
unavailable tenant links, server pagination/search, cancelled page reads, errors and Viewer access.
The repository's `BrowserSmokeE2ETests` uses real Keycloak/API/Kubernetes for image deployment,
configuration updates, tab keyboard controls, draft retention, reload, confirmation-dialog focus,
group metadata drafts/saves, tree/provider navigation, both themes and desktop/narrow screenshots
of each workspace section. `SourceBuildE2ETests` also covers browser Git creation, recipe edits
across refresh, real artifact/reconciliation results, search/command navigation and failed builds.
Its prerequisites and cleanup are documented in
[Aspire E2E testing](../../../docs/aspire-e2e-testing.md). Publication requires the full
[local quality gate](../../../docs/agent-delivery.md).

### Operations and observed health

Tenant overview and group Operations show paginated durable deployment state with whole-scope
reconciliation totals and independently observed targets. WebService, Redis and PostgreSQL have
Operations sections; their overview also reports runtime readiness. Applied and Ready are separate
signals. WebServices additionally display retained build/release operations and existing trace and
correlation identifiers. Owner-supported retries confirm one selected operation and carry its
expected revision/operation. Closing observations does not cancel accepted server work; reload
fetches durable state. Read errors mark previous evidence stale, unavailable targets remain visible,
and API/session/tenant changes cancel old reads. Token renewal retains ordinary data and drafts and supplies the latest credential to the next request. Database pod readiness does not establish
application connectivity. Each deployment also offers opt-in runtime logs and Kubernetes warning
events through the shared viewer. Discovery supplies verified instance UIDs and regular/init
container choices; the browser never supplies a namespace or arbitrary pod name. Reads are bounded
and cancel on close, hidden section, tenant, target or deployment revision/generation changes. Token renewal keeps the current bounded buffer; subsequent requests obtain current credentials.
Replacement requires refreshed discovery and explicit selection; reconnect opens a new recent
window and can overlap output. No runtime output enters browser storage or a platform archive.
See [runtime diagnostics](../../features/ResourceDeployments/README.md#runtime-logs-and-kubernetes-diagnostics).

The WebService Releases section owns paginated history and explicit redeploy/rollback recovery.
It shows actor/time, per-target apply results, source-build/CI links and safe configuration counts.
Pending releases refresh faster and survive reload; stale reads retain the loaded page and errors
keep the recovery selection. Both mutations carry the confirmed active revision. Rollback is
offered only for succeeded prior releases, with backend authority under races. Confirmations
explain current placement/exposure/compute preservation and the limits around Secret values,
database contents and movable image tags. Viewer has read-only history.

Builds and Operations share an authorized build-log viewer. A build fragment link opens an exact
job even outside the recent-build page. Follow and reconnect request only later persisted sequences;
pause/close affect browser reads alone. Fixed transport errors retain the displayed prefix and
cursor. The viewer marks server truncation, interrupted capture, expiry and unavailability, renders
plain text, and limits its tail to 256 KiB/512 entries with an explicit discarded-range notice.
Changing session, tenant, service or job aborts old reads and clears the buffer. Token renewal preserves the in-memory buffer and cursor. Logs stay on the
server until their snapshotted retention deadline; output and cursor are not saved in browser storage.

## Backup workflows

The Backups view connects tenant-owned S3 destinations through existing Secret references, configures
UTC schedules and recovery-window retention, and observes backup/restore operations. Destinations
inherit the selected Secret's resource group and isolation scope. PostgreSQL lists current completed
copies on demand and supports either restoring a selected copy or choosing a recovery time. The
browser converts local recovery time to an explicit UTC instant before requesting confirmation.
The new cluster is manually managed on the source deployment target. Archive selection, restore
inputs and pending commands belong to the current authenticated tenant. Metadata stays in scoped
queries, while operational commands use stable idempotency keys across an uncertain retry.

## Realtime query ownership

One account-scoped cookie connection follows the authenticated query session independently of
tenant workspaces and dirty drafts. Visible active queries register their owning metadata topics;
server subscription acknowledgement determines coverage per topic. Reconnect acknowledges first,
then refetches active authoritative HTTP queries. Hints only invalidate metadata and advance scoped
observation fences; they never insert resource DTOs or grant permissions. Bursts coalesce for 250 ms,
keep slow reads alive and schedule one trailing read. Healthy heartbeats do not create HTTP churn.

Covered metadata retains a 60-second safety refresh; disabled or unhealthy transport restores each
screen's existing fallback interval. WebService, Redis and PostgreSQL runtime health/diagnostics
use 30-second covered validation and 20-second uncovered reads. Health evidence becomes stale
at 45 seconds. Admin storage discovery uses one complete sanitized target snapshot, 30-second
covered validation and its existing initial/manual/visibility uncovered behavior. Storage evidence
also expires at 45 seconds; stale or incomplete inventory cannot enable a profile save.

Storage snapshot keys and read epochs include the exact target connection version. Catalog revision
is distinct from connection version: a target catalog hint fences the old native read before target
inventory refresh. Complete prior evidence may remain explicitly stale for the same target, keeping
a dirty profile form mounted through ordinary hints, renewal and rotation. Incomplete responses
cannot replace its last complete rows or class choices. Permission loss clears privileged metadata
and drafts; restoration waits for current fresh admin authority and new reads. Tenant memberships,
ordinary tenant drafts and account discovery retain their independent ownership.

Connection endpoints are declared provider metadata and persisted source/destination topology.
Their queries observe the destination/source provider and committed registration/placement hints;
dependency-field composites retain exact destination identities. Unknown providers retain 20-second
fallback until their metadata capability is known. This path does not claim live EndpointSlice
coverage, endpoint readiness, network enforcement or database connectivity.

Visibility releases observation interests and suspends native/query work. Bearer renewal preserves
ordinary drafts and authorized log reads; tenant/session/permission loss clears sensitive ownership.
Passwords, credentials and runtime/build output remain outside notification payloads, query/mutation
caches, URLs and persisted workspace references. Runtime logs remain short-lived bounded HTTP streams.

## Formatting

Prettier is pinned as a development dependency. Run `npm run format` to format frontend
source, styles and configuration, or `npm run format:check` to verify them. `npm run lint`
also checks formatting. Generated output, lockfiles and public assets are excluded.

The frontend uses two-space indentation, single quotes, no optional semicolons and a
100-column print width. Keep semantic lint rules in ESLint; Prettier owns layout.
