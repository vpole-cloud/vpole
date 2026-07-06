---
name: pole-github-issue-planning
description: Workflow for planning, deduplicating, drafting, prioritizing, and optionally creating GitHub issues for Pole maintenance and improvement work. Use when scanning the Pole repo for tickets, building a backlog, creating issue drafts, coordinating specialist issue scouts, labeling issues, checking existing GitHub issues for duplicates, or publishing selected issues to GitHub.
---

# Pole GitHub Issue Planning

Use this skill to turn repo knowledge into useful GitHub issues without creating noisy or duplicate backlog items.

## Operating Modes

- Draft mode: produce issue drafts only. Do not write to GitHub.
- Review mode: produce a deduplicated candidate list and ask which issues to create.
- Publish mode: create GitHub issues after duplicate checks. Use this only when the user explicitly asks to publish or confirms selected drafts.

Default to review mode unless the user clearly requests draft-only or publish mode.

## Coordinator Rules

- Only the coordinator may create GitHub issues.
- Specialist scouts produce candidate drafts only.
- Check existing GitHub issues before creating new ones.
- Prefer the `$github` skill and GitHub connector for issue lookup and creation. Use local `gh` only when connector coverage is insufficient.
- Restate the exact issue titles and labels before publishing unless the user already approved a concrete list.
- Keep issues small enough to implement independently. Use epics only for genuinely multi-ticket work.

## Discovery Workflow

1. Inspect the relevant feature and nearby implementation before editing.
2. Read high-signal docs based on scope: `README.md`, `docs/architecture_overview.md`, `docs/testing.md`, `docs/multitenancy.md`, `docs/observability.md`, `docs/search.md`, and `docs/feature-boundary-decoupling-plan.md`.
3. Inspect current GitHub issues for duplicates and already-planned work.
4. Split scouting by area: backend, tenant security, Kubernetes infrastructure, testing, frontend product, and docs/devex.
5. Merge overlapping proposals, remove vague ideas, and rank remaining candidates.

## Issue Quality Bar

A good Pole issue has:

- A concrete problem, not just a broad theme.
- A reason it matters for users, operators, contributors, or maintainers.
- A scoped implementation target.
- Acceptance criteria that can be verified.
- Test or validation expectations.
- Related files, docs, or concepts when known.
- Labels that describe area, type, and priority.

Avoid tickets that are only "clean up code", "improve UI", or "add tests" without a specific failure mode or target behavior.

## Issue Template

Use this body shape unless GitHub issue templates require something else:

```md
## Problem

## Why It Matters

## Proposed Scope

## Acceptance Criteria

## Technical Notes

## Tests / Validation

## Related Areas
```

## Suggested Labels

Area labels:

- `area:backend`
- `area:frontend`
- `area:kubernetes`
- `area:testing`
- `area:docs`
- `area:tenant-security`
- `area:observability`

Type labels:

- `type:bug`
- `type:task`
- `type:refactor`
- `type:maintenance`
- `type:epic`

Priority labels:

- `priority:p0`
- `priority:p1`
- `priority:p2`
- `priority:p3`

Use only labels that exist in the repository, or ask before creating missing labels.

## Scout Focus

- Backend maintenance: feature boundaries, handlers, domain factories, DI, Mongo, mediator, validation, search projection shape, and F#/C# consistency.
- Tenant security: tenant filters, marker interfaces, role checks, platform-admin behavior, active tenant header flow, and cross-tenant tests.
- Kubernetes infrastructure: namespace rules, labels, resource managers, manifest shape, storage, ingress, secrets, cleanup, and live cluster assumptions.
- Test quality: missing regression coverage, overly expensive boundaries, flaky infrastructure assumptions, and test helper gaps.
- Frontend product: API typing drift, missing resource pages, tenant UX, form/list ergonomics, loading/error states, and control-plane workflows.
- Docs/devex: stale docs, setup gaps, local development friction, testing instructions, architecture notes, and contributor onboarding.

## Candidate Ranking

Rank candidates by:

1. User impact or security risk.
2. Likelihood of bugs or maintenance drag.
3. Whether the issue unblocks future roadmap work.
4. Scope clarity and implementability.
5. Availability of reliable validation.

## Publishing Checklist

Before creating issues:

- Search existing open and recently closed issues for similar titles and keywords.
- Remove duplicates and merge overlapping scopes.
- Confirm labels and milestone if the user expects them.
- Ensure each issue has acceptance criteria and validation notes.
- Create issues one by one through `$github` or the connector-backed GitHub workflow.
- Return created issue URLs and any skipped candidates with reasons.