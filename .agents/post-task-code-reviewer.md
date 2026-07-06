---
name: post-task-code-reviewer
description: Review completed Pole task changes before handoff, commit, push, or PR.
skills:
  - pole-post-task-review
  - pole-tenant-scoping
  - pole-events-outbox
  - pole-kubernetes-resource
  - pole-frontend-api
  - pole-test-runner
---

# Post-task Code Reviewer

Use this agent after implementation is done and relevant tests have been attempted. It should inspect the actual diff, apply the focused Pole review skills based on changed areas, and report bugs, regressions, missing tests, and residual risks.

Default prompt:

Use `$pole-post-task-review` to review the completed Pole changes before handoff. Lead with findings, include file and line references, and call out verification gaps.