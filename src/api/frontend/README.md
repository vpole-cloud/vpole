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
services through `useAppServices`. API functions remain centralized in `src/api.ts`; tenant
requests use bearer authentication and `X-Pole-Tenant-ID`. View access and disabled actions guide
the user; the server remains authoritative for authorization.

Views remount on user/tenant changes. Cleanup aborts their scoped reads and prevents previous-view
state from entering the new workspace. A pending server mutation continues after its view closes;
completion notifications from a previous tenant are ignored. Neither tokens nor form/secret values
are persisted by the shell. Theme preference is stored in `pole.console.theme`; tenant selection
retains `pole.activeTenantId`.

The header has one tenant selector with the membership role, plus theme and login/logout actions.
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

## Resource workspaces

PostgreSQL contributes its own catalog/create/resource workspace (`pole/postgresql`) and scoped
command. Overview, Configuration and privileged Credentials sections use a single deployment.
Creation reads the supported-version range and server-selected provisioning mode, named group
scopes, eligible targets and compute/storage profiles. Storage profiles are filtered by their
target mappings. Native StatefulSet mode supports one instance and cannot rotate an initialized
database password; CloudNativePG supports the existing managed-secret rotation API.
Name, version, group, target and storage profile are fixed after creation. Updates preserve the
current resolved compute profile unless the user explicitly selects an eligible replacement.
Native storage is fixed after creation; operator-managed storage can grow.
Lifecycle, reconciliation generation and failure codes are shown without inferring DB readiness.
Group tags are metadata of the group; the database API has no separate editable tags.

PostgreSQL workspace persistence saves only identities and sections under
`pole.postgres.workspace.v1.<user-id>.<tenant-id>`. Initial username/password clear when creation
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
and API/token/tenant changes abort and clear old results. Database pod readiness does not establish
application connectivity. Each deployment also offers opt-in runtime logs and Kubernetes warning
events through the shared viewer. Discovery supplies verified instance UIDs and regular/init
container choices; the browser never supplies a namespace or arbitrary pod name. Reads are bounded
and cancel on close, hidden section, tenant, token, target or deployment revision/generation changes.
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
Changing tenant, token, service or job aborts old reads and clears the buffer. Logs stay on the
server until their snapshotted retention deadline; output and cursor are not saved in browser storage.
