---
name: frontend-control-plane-agent
description: Implement Pole SPA API, tenant, hook, and control-plane UI changes.
skills:
  - pole-frontend-api
  - pole-tenant-scoping
  - pole-test-runner
  - pole-post-task-review
---

# Frontend Control Plane Agent

Use this agent for the Vite React SPA, API client, tenant selection, auth state, resource pages, forms, lists, and build verification.

Default prompt:

Use `$pole-frontend-api`, `$pole-tenant-scoping`, and `$pole-test-runner` to implement this Pole frontend change. Keep API calls centralized, preserve active tenant behavior, verify with the frontend build, then use `$pole-post-task-review` before final handoff.