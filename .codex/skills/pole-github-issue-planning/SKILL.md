---
name: pole-github-issue-planning
description: Draft, deduplicate, prioritize, or publish well-scoped GitHub issues for Pole.
---

# Pole GitHub Issue Planning

Use this skill to turn repo knowledge into useful GitHub issues without creating noisy or duplicate backlog items.

Work in draft/review mode unless the user explicitly asks to create issues or approves a concrete list. Publishing is an external write and requires that authorization even when the drafts are complete.

## Discovery Workflow

1. Inspect the relevant implementation and only the docs that govern that area.
2. Search open and recently closed GitHub issues for the same behavior, terminology, and affected component.
3. Merge overlapping candidates and discard ideas without a concrete problem or verifiable outcome.
4. Rank by user or security impact, bug likelihood, roadmap leverage, scope clarity, and available validation.
5. Use parallel read-only scans only when several independent repository areas genuinely need exploration.

## Issue Quality Bar

A useful issue has:

- A concrete problem, not just a broad theme.
- A reason it matters for users, operators, contributors, or maintainers.
- A scoped implementation target.
- Acceptance criteria that can be verified.
- Test or validation expectations.
- Related files, docs, or concepts when known.
- Existing repository labels that describe it accurately.

Avoid broad cleanup, UI, or testing tickets without a specific failure mode or target behavior. Keep issues independently implementable; use an epic only when coordination across multiple tickets is intrinsic.

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

## Publishing

Before creating issues:

- Re-run the duplicate search against current GitHub state.
- Verify every requested label and milestone exists; do not create metadata without separate approval.
- Use the available GitHub connector when present, otherwise `gh`.
- Create only the approved drafts.
- Return created issue URLs and any skipped candidates with reasons.
