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

## Current slice and next stage

WebService uses the module contract for the existing image/configuration, deployment, hostname,
release and source-build screens. Successful image creation selects the created service and reports
configuration acceptance. `Applied` reports reconciliation, not observed application readiness.
All existing overview, resource-group, domain, Git integration and storage screens remain registered.

Navigation supports registered `?view=` links and browser Back/Forward. Resource trees, resource
URLs, persistent tabs, dirty-form protection and workspace layout persistence belong to #12.
This slice does not complete #90's whole Git/image journey or #96's console release. GitOps
ownership and source management belong to #88; runtime diagnostics and readiness belong to #8/#95.

Shared components in `src/console/components.tsx` cover buttons, fields, tables, status, notices,
empty/error states and accessible confirmation dialogs. Semantic tokens in `src/index.css` provide
light/dark themes, with responsive layout in `src/App.css`.

## Verification

Run `npm test`, `npm run build` and `npm run lint` from this directory. Tests include independent
module composition, registration conflicts, tenant races, image mutation/errors and Viewer access.
The repository's `BrowserSmokeE2ETests` uses real Keycloak/API/Kubernetes for image deployment,
reload, confirmation-dialog keyboard focus, both themes and desktop/narrow screenshots. Its prerequisites and cleanup are documented in
[Aspire E2E testing](../../../docs/aspire-e2e-testing.md). Publication requires the full
[local quality gate](../../../docs/agent-delivery.md).
