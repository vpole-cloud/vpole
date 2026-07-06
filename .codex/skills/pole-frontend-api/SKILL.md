---
name: pole-frontend-api
description: Workflow for updating Pole's Vite React frontend and API client. Use when editing `src/api/frontend/src/api.ts`, `types.ts`, hooks, auth, tenant selection, control-plane pages, resource forms/lists, or when adding frontend support for backend resource APIs that require bearer tokens or `X-Pole-Tenant-ID`.
---

# Pole Frontend API

Use this skill to keep the SPA aligned with backend contracts and tenant behavior.

## First Steps

1. Inspect the relevant feature and nearby implementation before editing.
2. Inspect `src/api/frontend/src/api.ts`, `types.ts`, and the closest hook/page pair.
3. Check backend endpoint DTOs before changing frontend types.

## API Client

- Keep HTTP helpers in `api.ts`; do not scatter raw fetch calls through components.
- Use `buildHeaders(token, tenantId)` for tenant-owned resources so `Authorization` and `X-Pole-Tenant-ID` are threaded consistently.
- Do not send a tenant header to tenant discovery calls unless the backend endpoint requires it.
- Keep request body shaping in `api.ts` when backend DTOs differ from UI form state.
- Throw backend response text when available so hooks can display useful errors.

## State And Hooks

- Use existing hooks such as `useTenants`, `useResourceGroupsManager`, `useWebServicesManager`, and `useOverviewData` as patterns.
- Hooks should own loading/error state and cancellation where nearby hooks already do that.
- Components should stay focused on rendering, form state, and user actions.
- Preserve active tenant storage under `pole.activeTenantId`.

## UI Conventions

- Keep the app as an operational control plane, not a marketing page.
- Match existing shell structure: `AppShell`, `SideNav`, `TopBandPlaceholder`, and feature pages.
- Keep forms and lists dense enough for repeated admin work.
- Validate that text fits in compact controls and cards across mobile and desktop.

## Backend Alignment

- Update frontend `types.ts` when backend DTO names or shapes change.
- Add API functions and hooks together so pages do not duplicate fetch logic.
- Ensure create/update/delete calls include the active tenant id for tenant-owned resources.
- For new resource types, add navigation and overview behavior only when there is a clear user workflow.

## Validation

- Run `npm run build` from `src/api/frontend` after TypeScript or component changes.
- If visual behavior changes materially, run the app and verify the relevant viewport with a browser screenshot.