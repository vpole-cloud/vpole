---
name: pole-deliver-issues
description: Coordinate a batch of Pole GitHub issues through prioritization, bounded delegation in separate worktrees, verified pull requests, and authorized sequential merges. Use for multi-issue delivery or an explicit team-lead workflow; use feature or fix-bug for an ordinary single implementation.
---

# Pole Issue Delivery

The primary agent owns product decisions, coordination, acceptance and the final result,
including delegated work. Compose bounded assignments; do not create persistent agent personas.

Read `AGENTS.md`, [agent delivery](../../../docs/agent-delivery.md), and the relevant feature
documentation. Those sources own engineering rules, canonical examples and publication gates.
Use `$pole-github-issue-planning` for issue analysis, `$pole-test-runner` for verification,
and `$pole-post-task-review` for review when those stages apply.

## Establish scope and choose work

- Respect the requested stage: discussion, investigation, implementation, PR publication or merge.
  Discussion-only and “stage 1” requests do not authorize later stages. Preserve permissions already
  granted in the conversation; only request missing authorization when that action is ready.
- Read current issues and repository state. Propose a small coherent batch based on user value,
  dependencies, risk and overlap; do not select tasks merely because they are easy to parallelize.
- For each issue, identify the owning feature, observable acceptance criteria, exclusions and
  dependencies. Resolve product assumptions that affect contracts or lifecycle before coding:
  identity, name uniqueness, deletion/recreation, authorization and startup/admission ordering.
  Use current project context rather than assuming production compatibility or frontend priority.
- Keep architectural decisions in their owning documentation and reference them from the brief.
  Future plans such as IaC do not authorize implementing unrelated infrastructure.

## Delegate with a concrete brief

Use the [assignment template](references/templates.md#agent-assignment) for each independent writer.
Give each writer its own worktree and, when branch creation is authorized, its own task branch.
Aim for one reviewable PR per issue; explicitly agree on a different split when dependencies require it.
Route shared contract edits through one owner or order the dependent tasks instead of allowing
competing designs. Choose available agents/models for the task, without fixed model names.

Include exact canonical source files, not just a request to “follow repository style.” Require the
representative-slice checkpoint from agent delivery before replicating a new endpoint pattern.
The primary agent inspects the actual slice and corrects mistakes before they spread; this internal
checkpoint is not an extra user approval gate.

Track issue, owner, worktree/branch, base commit, dependencies, current phase, review findings and
verification evidence in ignored task notes. Treat an agent's completion message as a handoff to
inspect, not proof that acceptance criteria or checks passed.

## Coordinate verification and fixes

Follow [test scheduling](../../../docs/testing.md#parallelism-across-worktrees). Test isolation and
host capacity are separate concerns: Docker tests can overlap across worktrees; Kubernetes-backed
runs are isolated too, but queue them by default because they are heavy. The primary agent schedules
heavy runs across the whole batch. Within one worktree, preserve build/output and test-project
serialization rules. The full quality-gate command retains its own sequential execution.

Use focused checks during development and the full local gate before publication, exactly as
specified in agent delivery. Missing infrastructure, inherited failures, or a draft PR do not waive
that gate. Diagnose failed prerequisites and use scoped recovery; do not reset a shared lab while
other runs are active. Reproduce bugs at the relevant boundary, fix their cause and retain meaningful
regression coverage. Repeated defects should improve the existing rule, test or canonical example.

Inspect startup and the real user flow when the change crosses those boundaries: passing handler
or manifest tests alone cannot establish that the API starts or a browser displays a successful write.

## Review, publish and integrate

- Complete self-review and the required independent read-only review of the exact diff. Give the
  reviewer acceptance criteria, base/target, actual checks and remaining uncertainties. Follow the
  repository's limit on follow-up reviews; the primary agent owns finding resolution.
- Record real full-run and review evidence with the existing quality-gate commands. Source or base
  changes require renewed evidence under that gate; never relabel an old successful run as current.
- Publish only within the user's authorization and through the documented PR wrapper. Each PR needs
  its own evidence and explanation. An agent must not open a draft while required tests are blocked.
- If merge is authorized, use the dependency order. Rebase onto the actual merged base, resolve
  conflicts deliberately, satisfy the full local gate and await green CI for the exact head before
  each merge. Use a head-match guard and preserve linear history. Honor “do not merge” independently
  of permission to implement or publish.

## Explain the result

Use the [PR handoff template](references/templates.md#pr-handoff). Explain the problem, resulting
behavior and end-to-end flow, important files, test evidence and material limitations for each PR.
If the user asks to discuss PRs one at a time, present one and wait before advancing that discussion.

Apply the failure-first summary rule from agent delivery: current failed or unavailable suites and
known causes come first. Distinguish an observed failure, a reproduced cause and a hypothesis.
After a successful complete rerun, describe historical failures as historical rather than repeatedly
reporting them as current blockers. Progress updates should identify changed facts or the remaining
dependency; use one CI watcher per run instead of repeatedly launching network status commands.
