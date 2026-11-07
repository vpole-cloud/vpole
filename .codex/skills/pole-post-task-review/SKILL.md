---
name: pole-post-task-review
description: Review a completed Pole diff for correctness and verification gaps, either as self-review or as an independent read-only pre-publish review.
---

# Pole Post-task Review

Use this skill after implementation and targeted verification, before final response or publishing changes.

## Review Mode

- **Self-review:** The implementing agent inspects and corrects its own completed change before handoff.
- **Independent pre-publish review:** A fresh built-in agent reviews a non-trivial code change before an authorized push or pull request. It is read-only, makes no fixes, and must not delegate to another agent.
- **Review-only request:** When the user asks only for review, report findings without changing files.

For independent review, use the supplied request or issue, approved plan when present, base branch or commit, diff target, test results, skipped checks, and known risks. Verify claims against the shared checkout and actual diff; treat the supplied summary as context rather than proof. If essential context is missing, resolve it read-only from the repository when possible and state any remaining uncertainty.

## Review Setup

1. Run `git status --short` and identify whether the review target is the working tree or a branch/commit range.
2. Inspect the diff stat, complete changed-file list, and relevant diff slices against the supplied base.
3. Read the [review checklist](../../../docs/review-checklist.md) and its canonical engineering/module rules. For code changes, inspect the owning feature and related callers, serialization, alternate write paths and tests. For documentation, skills, or agent guidance, inspect the governing source and affected references. Honor any explicitly requested repository-wide audit without adding unrelated refactoring.
4. Identify the changed areas and load the matching focused skills only when relevant:
   - `$pole-tenant-scoping` for tenant-owned resources, auth, headers, Mongo filters, search tenant data, or frontend tenant flow.
   - `$pole-events-outbox` for events, outbox, consumers, trace/correlation, DNS reservation messages, or transaction boundaries.
   - `$pole-kubernetes-resource` for namespace, manifest, resource manager, storage, ingress, or live cluster behavior.
   - `$pole-frontend-api` for SPA API calls, hooks, tenant selection, forms, pages, or TypeScript types.
   - `$pole-test-runner` for deciding missing or insufficient validation.

## Review Priorities

Work through R1–R8 in the checklist. Prioritize correctness, tenant/authority failures, data loss
and regressions, then design and readability. Code style and Pole's agreed design rules are part
of the review even when the build passes.

Distinguish automated coverage from manual inspection. Verify current reports and the actual
analyzers in the reviewed tree; do not infer complete coverage from a diagnostic name. Record
each manual area's disposition and supporting paths or evidence. Group areas that are not
applicable with a reason, and report missing evidence as unverified. A checked box without
inspection is not a review result.

Use the canonical linked policies rather than copying a private observation history into the
review. Preserve documented exceptions and leave deferred proposals outside the agreed scope.

## Output Format

Use the repo's normal code-review stance:

1. If tests fail, first state which tests fail and why. Then list findings ordered by severity.
2. Include file/line references, the applicable canonical rule and a proposed correction for each finding.
3. Explain the failure mode and the concrete consequence; distinguish a defect from a deferred proposal.
4. Add open questions or assumptions only after findings.
5. Add a short verification note covering what was run and what was not run.
6. If there are no findings, say that clearly and mention any residual test gaps or risk.
7. Return a compact [review record](../../../docs/review-checklist.md#compact-review-record) with the exact target, automated evidence and R1–R8 dispositions. The primary agent may save it to ignored notes; an independent reviewer remains read-only. A handoff may link the saved record instead of repeating it.

## Self-review

When this skill is used by the same agent that implemented the task:

- Fix clear, local defects before final handoff when doing so is within the original task scope.
- Re-run the relevant validation after fixes.
- Follow the agreed commit grouping for findings and hand the user a committed result with SHAs; do not infer permission to commit or publish from this skill.
- Report only unresolved findings, verification gaps, and material follow-up risks in the final handoff.
- Do not hide meaningful review findings just because they were found late.

## Independent Or Review-only

Do not edit files, commit, push, publish, or spawn more agents. Provide findings first, then questions or assumptions, then a brief verification summary. A clean review must say so explicitly and retain any residual risk or unavailable test lane.

## Delivery Gate

Follow `docs/agent-delivery.md`. Focused checks are development feedback; before opening any PR, run `python3 scripts/quality_gate.py run` on the final source tree. Every local backend, frontend, automation, and infrastructure test must pass with zero skips, including for documentation or tooling PRs. Missing prerequisites and pre-existing failures block publication; draft PRs are not an exception. Use the recorded full-run evidence and review required by AGENTS.md with the publication wrapper described there.

If tests fail, the first sentence of every summary must identify the failing tests and explain the known cause (or say it is still unknown). Report unavailable infrastructure as a blocker, never as a passed test.
