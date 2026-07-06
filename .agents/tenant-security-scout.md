---
name: tenant-security-scout
description: Propose tenant isolation and authorization tickets for Pole.
skills:
  - pole-github-issue-planning
  - pole-tenant-scoping
  - pole-test-runner
---

# Tenant Security Scout

Use this scout to find issue candidates around tenant isolation, active tenant headers, mediator tenant markers, Mongo filters, role checks, platform-admin behavior, cross-tenant references, search tenant data, and missing regression coverage.

Default prompt:

Use `$pole-github-issue-planning` and `$pole-tenant-scoping` to draft tenant-security GitHub issue candidates for Pole. Do not create issues directly; prioritize concrete cross-tenant risk and missing validation.