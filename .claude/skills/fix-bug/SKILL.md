---
name: fix-bug
description: Diagnose and fix a Pole bug from a GitHub issue or reproduction report. Use when the workflow should prove the failure, identify the root cause, obtain approval, and make the smallest verified correction.
argument-hint: "<issue-id or bug report>"
---

# Pole Bug Fix

Accept a GitHub issue ID or a concrete bug report: $ARGUMENTS

## Diagnose Before Editing Production Code

1. Read the ticket when supplied and trace the affected flow through the `pole-flow-docs` skill without writing documentation unless requested.
2. Reproduce the failure at the cheapest boundary that still proves the bug.
3. State the root cause with file and symbol references, the proposed regression-test seam, the minimal fix, affected contracts, and focused validation.
4. If a deterministic regression test is not feasible, explain the limiting boundary and propose the strongest available evidence instead.
5. Wait for explicit approval before editing repository files.

## Fix And Verify

- After approval, if the user authorized branch creation, verify the working tree and create or switch to the task branch before implementation edits.
- Use the [first-slice checkpoint](../../../docs/agent-delivery.md#review-the-first-slice-before-repeating-it)
  when introducing a new or repeated pattern; isolated fixes retain its documented exemption.
- Add a focused regression test and run it to demonstrate the expected failure before changing production code.
- Make the smallest change that addresses the demonstrated cause, not adjacent cleanup.
- Make the regression test pass and run the focused owning suite selected through the `pole-test-runner` skill.
- Broaden intermediate validation when the changed boundary or risk requires it; the selected publication verification below is mandatory.
- Use the `pole-post-task-review` skill on the complete diff and correct clear in-scope defects.

Report the reproduction, root cause, change, regression evidence, other checks, skipped checks, and remaining risk.

Do not commit, push, create a branch, or open a pull request without explicit user authorization for that action.

## Delivery Gate

Follow `docs/agent-delivery.md` for selection from the complete PR diff. Run `python3 scripts/quality_gate.py plan` and `run --profile pr`: docs/proven formatting use structural checks; templates use generated-code checks; behavioral or unproven changes retain complete PR suites. Use `--profile full` for broad/infrastructure work or explicitly fresh all-lane evidence. Selected tests require zero failures/skips; unavailable unselected infrastructure does not block reduced verification. Reuse behavioral results only through the gate's committed same-base safe-delta proof, recording both SHAs; never relabel stale results manually. Use `record-review`, `verify` and the authorized publication wrapper.

If tests fail, the first sentence of every summary must identify the failing tests and explain the known cause (or say it is still unknown). Report unavailable selected infrastructure as a blocker, never as a passed test.
