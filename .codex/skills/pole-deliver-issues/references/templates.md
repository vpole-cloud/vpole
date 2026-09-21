# Delivery templates

Fill only the fields that help the current assignment. Link canonical rules rather than copying them.

## Agent assignment

- **Issue and user outcome:** issue link, problem and observable acceptance criteria.
- **Scope and decisions:** owning feature, exclusions, approved product decisions and their source.
- **Workspace:** absolute worktree, branch (if authorized), base commit, owned files/areas and dependencies.
- **References:** required feature docs and exact compiled canonical files for the boundaries being changed.
- **Failure cases:** expected errors, tenant boundaries, races, lifecycle/startup behavior relevant to this task.
- **Early checkpoint:** representative complete slice to return before repeating a new pattern.
- **Verification:** focused behavior checks, prerequisites and a request to the primary agent for heavy-run scheduling;
  full local publication gate and review remain mandatory under `docs/agent-delivery.md`.
- **Authority and handoff:** permitted actions, whether PR publication/merge is authorized, remaining blockers,
  actual diff and log/report paths. Return to the primary agent before actions requiring its acceptance.

## PR handoff

If a required suite currently fails or cannot run, the first sentence names it and explains the
known cause or missing prerequisite; say diagnosis is pending when the cause is unknown.

- **Problem and outcome:** concrete before/after behavior and the issue addressed.
- **Flow:** trigger → validation/admission → persistence/events → external effects or visible result,
  using only the stages this change actually has.
- **Review map:** important files and the decisions/invariants to inspect in each.
- **Evidence:** tested source/base, full local gate receipt, counts and failures/skips, review findings
  and resolutions, CI run/head when available. Label focused checks separately from the full gate.
- **Limits and state:** known risks, deliberately separate work, current PR state and any missing authorization.

Write the PR description for someone who has not read the conversation. Scale detail to the diff;
do not copy internal agent scheduling or the complete discussion into it.
