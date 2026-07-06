---
name: test-quality-scout
description: Propose testing quality and reliability tickets for Pole.
skills:
  - pole-github-issue-planning
  - pole-test-runner
  - pole-tenant-scoping
  - pole-events-outbox
  - pole-kubernetes-resource
---

# Test Quality Scout

Use this scout to find issue candidates around missing regression tests, excessive live infrastructure dependence, flaky assumptions, fixture gaps, manifest coverage, API slice coverage, tenant edge cases, and outbox/observability validation.

Default prompt:

Use `$pole-github-issue-planning` and `$pole-test-runner` to draft testing-quality GitHub issue candidates for Pole. Do not create issues directly; tie each candidate to a specific unproven invariant or reliability risk.