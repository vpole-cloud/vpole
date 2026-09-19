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

## Delivery Gate

Follow `docs/agent-delivery.md`. Focused checks are development feedback; before opening any PR, run `python3 scripts/quality_gate.py run` on the final source tree. Every local backend, frontend, automation, and infrastructure test must pass with zero skips, including for documentation or tooling PRs. Missing prerequisites and pre-existing failures block publication; draft PRs are not an exception. Use the recorded full-run evidence and review required by AGENTS.md with the publication wrapper described there.

If tests fail, the first sentence of every summary must identify the failing tests and explain the known cause (or say it is still unknown). Report unavailable infrastructure as a blocker, never as a passed test.

Before implementation, name the exact canonical compiled/tested files for the affected patterns. The primary agent reviews one representative slice against those references before the pattern is repeated; this is an internal checkpoint, not another user approval. Add a focused automated safeguard for recurring mistakes where the invariant is mechanically checkable. Existing explicit user approval satisfies the corresponding approval gate.
