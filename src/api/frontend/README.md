# Pole web console

The Vite/React console talks to Pole's authenticated API. Its initial static module boundary is
in `src/console/contracts.ts`. `src/modules/index.ts` explicitly composes trusted bundled modules;
the generic shell does not import resource providers.

## Module ownership

Modules contribute view registrations, stable resource type identities and contextual commands.
`createConsoleRegistry` rejects duplicate module/view/resource/command IDs and missing view
references. Providers own React views, hooks, DTOs and resource behavior. Registration metadata
stays in `.ts` files and components in `.tsx` files so React Fast Refresh works.

The shell exposes explicit auth, active tenant, API connection, navigation, theme and notification
services through `useConsoleServices`. API functions remain centralized in `src/api.ts`; tenant
requests use bearer authentication and `X-Pole-Tenant-ID`. View access and disabled actions guide
the user; the server remains authoritative for authorization.

Views remount on user/tenant changes. Cleanup aborts their scoped reads and prevents previous-view
state from entering the new workspace. A pending server mutation continues after its view closes;
completion notifications from a previous tenant are ignored. Neither tokens nor form/secret values
are persisted by the shell. Theme preference is stored in `pole.console.theme`; tenant selection
retains `pole.activeTenantId`.

Modules can register dirty forms with the shell's navigation service. Leaving a module, selecting
another tenant or logging out asks for confirmation; browser unload uses the native warning.
Browser Back/Forward uses a native confirmation when leaving a dirty view. Cancelling keeps the
current view and restores its URL.

## Current slice and next stage

WebService has an independent catalog, one creation tab and multiple open resource tabs. Each
resource offers Overview, Configuration, Deployments (including hostnames/releases) and Builds.
Visited sections remain mounted so drafts survive switching sections and resources. Closing a
dirty resource or creation tab asks for confirmation; successful saves clear the dirty marker.
Source configuration and placement/alias drafts participate in the same guard. Viewer access
remains read-only. A late creation completion refreshes the catalog without reopening a closed tab.

Successful image creation opens the resource's Overview and reports configuration acceptance.
`Applied` reports reconciliation, not observed application readiness. Restored resources are read
directly by ID, independently of the first catalog page; missing/unavailable resources show an
explicit retry state. All other overview, resource-group, domain, Git integration and storage
screens remain registered.

Workspace references are stored under `pole.web-services.workspace.v1.<user-id>.<tenant-id>`.
Only resource IDs, selected sections and the active resource/catalog tab are saved (up to 50
restored resources). Creation drafts, form contents, secrets and tokens stay in memory. Corrupt
or unavailable browser storage falls back to the catalog. Reload after confirming draft loss
loads the current server configuration.

Navigation supports registered `?view=` links and browser Back/Forward. This implements the
WebService workspace slice of #12; resource trees, resource URLs and other providers' workspaces
remain separate work.
This slice does not complete #90's whole Git/image journey or #96's console release. GitOps
ownership and source management belong to #88; runtime diagnostics and readiness belong to #8/#95.

Shared components in `src/console/components.tsx` cover buttons, fields, tables, status, notices,
empty/error states and accessible confirmation dialogs. Semantic tokens in `src/index.css` provide
light/dark themes, with responsive layout in `src/App.css`.

## Verification

Run `npm test`, `npm run build` and `npm run lint` from this directory. Tests include independent
module composition, registration conflicts, tenant races, image mutation/errors, separate drafts,
dirty-close/navigation/unload guards, restoration, missing resources and Viewer access.
The repository's `BrowserSmokeE2ETests` uses real Keycloak/API/Kubernetes for image deployment,
configuration updates, tab keyboard controls, draft retention, reload, confirmation-dialog focus,
both themes and desktop/narrow screenshots of each workspace section. Its prerequisites and cleanup are documented in
[Aspire E2E testing](../../../docs/aspire-e2e-testing.md). Publication requires the full
[local quality gate](../../../docs/agent-delivery.md).
