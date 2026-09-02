---
name: feature
description: Plan and implement a Pole feature from a GitHub issue or user brief. Use when a change needs explicit scope, staged implementation, verification, and a human approval gate before code changes.
---

# Pole Feature Delivery

Accept a GitHub issue ID or a user brief. Do not assume that a ticket is current or complete.

## Understand And Plan

1. Read the ticket when supplied, then inspect the affected code, nearby patterns, contracts, and focused Pole documentation.
2. Resolve repository facts directly. Use parallel exploration only for substantial independent read-heavy areas; the primary agent owns decisions and synthesis.
3. Present a staged plan containing scope, non-goals, contracts or compatibility changes, per-stage validation, migration concerns, and material risks.
4. Wait for explicit user approval before implementing. Discussion and read-only exploration are allowed before this gate.
5. After approval, if the user authorized branch creation, verify the working tree and create or switch to the task branch before implementation edits.
6. Save the approved plan to `.codex/notes/<issue-or-short-slug>.md` so another session can resume. This folder is local and ignored.

## Implement

- Work in small vertical stages that remain buildable and independently reviewable.
- Follow `$pole-add-feature-slice` when the work crosses feature layers, plus focused tenancy, outbox, Kubernetes, or frontend skills when applicable.
- Keep one primary writer. Delegate implementation only when ownership is disjoint and the coordination benefit is clear.
- End each stage with the smallest meaningful verification chosen through `$pole-test-runner`.
- If evidence invalidates the approved plan or expands scope materially, stop and return to the user with the new facts.

## Complete

Use `$pole-post-task-review` over the complete diff. Report implemented scope, files changed, test evidence, skipped checks, compatibility or migration effects, and residual risks.

Do not commit, push, create a branch, or open a pull request without explicit user authorization for that action.
