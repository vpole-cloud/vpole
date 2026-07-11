---
name: pole-post-task-review
description: Post-implementation review workflow for Pole changes. Use after a task is otherwise complete and before final handoff, commit, push, or PR. Reviews the actual git diff and changed files for correctness, regressions, missing tests, tenant isolation, event/outbox reliability, Kubernetes resource behavior, frontend API alignment, and verification gaps.
---

# Pole Post-task Review

Use this skill after implementation and targeted verification, before final response or publishing changes.

## Review Setup

1. Run `git status --short` and inspect the changed file list.
2. Run `git diff --stat` and the relevant `git diff -- <path>` slices.
3. Read `docs/engineering-guidelines.md` and inspect the relevant feature and nearby implementation.
4. Identify the changed areas and load the matching focused skills only when relevant:
   - `$pole-tenant-scoping` for tenant-owned resources, auth, headers, Mongo filters, search tenant data, or frontend tenant flow.
   - `$pole-events-outbox` for events, outbox, consumers, trace/correlation, DNS reservation messages, or transaction boundaries.
   - `$pole-kubernetes-resource` for namespace, manifest, resource manager, storage, ingress, or live cluster behavior.
   - `$pole-frontend-api` for SPA API calls, hooks, tenant selection, forms, pages, or TypeScript types.
   - `$pole-test-runner` for deciding missing or insufficient validation.

## Review Priorities

Review for bugs and regressions first, not style. Focus on:

- Incorrect behavior against the user request.
- Cross-tenant access, missing tenant headers, missing tenant filters, or unsafe platform-admin behavior.
- Domain mutations not persisted atomically with outbox messages.
- Events missing `EventCore.Create(tenantId)` or consumers ignoring `EventCore.TenantId`.
- Non-idempotent consumers or external side effects inside Mongo transactions.
- Kubernetes resources with wrong namespace, labels, selectors, names, ports, storage, ingress, or secret references.
- Frontend API calls that omit token or tenant id, duplicate fetch logic, or drift from backend DTOs.
- Tests that prove the wrong boundary, miss the changed invariant, or require live infrastructure unnecessarily.
- Generated files or unrelated edits that should not be part of the task.

## Output Format

Use the repo's normal code-review stance:

1. Lead with findings, ordered by severity.
2. Include file and line references for each finding when possible.
3. Explain the failure mode and the concrete consequence.
4. Add open questions or assumptions only after findings.
5. Add a short verification note covering what was run and what was not run.
6. If there are no findings, say that clearly and mention any residual test gaps or risk.

## Self-review Mode

When this skill is used by the same agent that implemented the task:

- Fix clear, local defects before final handoff when doing so is within the original task scope.
- Re-run the relevant validation after fixes.
- Report only unresolved findings, verification gaps, and material follow-up risks in the final handoff.
- Do not hide meaningful review findings just because they were found late.

## Review-only Mode

When the user explicitly asks for a review, do not make code changes unless asked. Provide findings first, then questions, then a brief summary.
