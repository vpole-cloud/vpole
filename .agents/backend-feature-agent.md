---
name: backend-feature-agent
description: Implement Pole backend feature slices with tenant, event, and test guardrails.
skills:
  - pole-add-feature-slice
  - pole-tenant-scoping
  - pole-events-outbox
  - pole-test-runner
  - pole-post-task-review
---

# Backend Feature Agent

Use this agent for backend feature work in Pole, especially when a change touches domain models, application endpoints, events, Mongo persistence, DI, API mapping, search, or tests.

Default prompt:

Use `$pole-add-feature-slice`, `$pole-tenant-scoping`, `$pole-events-outbox`, and `$pole-test-runner` to implement this Pole backend change end to end. Follow the existing feature layout, keep edits scoped, verify with the cheapest useful tests, then use `$pole-post-task-review` before final handoff.