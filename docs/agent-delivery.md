# Agent delivery and publication evidence

The primary agent owns the result, including work delegated to other agents. A
passing build is not evidence that endpoint ownership, domain invariants, or
the user flow were reviewed.

## Multi-issue delivery and current planning context

Use [pole-deliver-issues](../.codex/skills/pole-deliver-issues/SKILL.md) when coordinating a batch of
issues and delegated worktrees. It applies the rules here and links assignment/handoff templates;
it does not grant permission to publish or merge. Use [test scheduling](testing.md#parallelism-across-worktrees)
to distinguish test-resource isolation from host capacity.

Pole is currently in private, predeployment development. Backward compatibility with production
consumers is not a current constraint; coordinated code/API changes are acceptable. A substantial
frontend implementation is a separate initiative, so prioritize backend delivery and add only UI
pieces needed by the agreed scope. Revisit this context when deployment status or product priorities
change. Domain decisions, including resource identity and naming for future IaC, remain in their
owning feature/architecture documentation rather than being frozen into an orchestration skill.

## Task brief and canonical implementations

Before delegating implementation, name the owning feature, acceptance criteria,
exact files each writer owns, expected failure cases, and the checks that prove
the behavior. Include concrete reference files, not only a link to these rules.
Use the following maintained production implementations where their semantics fit:

| Boundary                                                                             | Compiled reference                                                                                                                                                                    | Evidence                                                                                                                                                                                                                    |
| ------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Endpoint, transport types, validator, handler, flat outcome and direct HTTP mappings | [CreateUser.cs](../src/features/Users/Users.Application/Endpoints/CreateUser.cs) and [UserHttpProblemMappings.cs](../src/features/Users/Users.Application/UserHttpProblemMappings.cs) | [UserControlPlaneTests.cs](../tests/Pole.Tests.Integration/Users/UserControlPlaneTests.cs), [ApiSliceTests.cs](../tests/Pole.Tests.Integration/Api/ApiSliceTests.cs) and architecture tests                                 |
| Provider-owned named contract outcomes                                               | [TenantIdentityContracts.cs](../src/features/Tenants/Tenants.Contracts/TenantIdentityContracts.cs)                                                                                    | [TenantFeatureProductionDiTests.cs](../tests/Pole.Tests.Integration/Tenants/TenantFeatureProductionDiTests.cs)                                                                                                              |
| Named persistence capability, internal Mongo implementation, atomic outbox           | [PersistCreatedRedis.cs](../src/features/RedisServices/RedisServices.Application/Services/PersistCreatedRedis.cs)                                                                     | `Create_Succeeds_WithValidRequest` and `PersistCreate_AbortsRedisAndOutbox_WhenTransactionFails` in RedisIntegrationTests                                                                                                   |
| Factory, immutable collections, explicit lifecycle transitions                       | [Redis.cs](../src/features/RedisServices/RedisServices.Domain/Redis.cs)                                                                                                               | `PersistUpdate_RejectsADeletingReplacement` and update/deletion cases in RedisIntegrationTests                                                                                                                              |
| Mongo session ownership and explicit commit/abort                                    | [MongoTransactionDecision.cs](../src/shared/Database/MongoTransactionDecision.cs) and [MongoTransactionPersistence.cs](../src/shared/Database/MongoTransactionPersistence.cs)         | [ClientSessionHandleExtensionsTests.cs](../tests/Pole.Tests.Integration/Database/ClientSessionHandleExtensionsTests.cs) and [OutboxPersistenceTests.cs](../tests/Pole.Tests.Integration/Database/OutboxPersistenceTests.cs) |

These are real compiled and tested source files, not copied sample implementations
that can drift. Read the owning [Redis documentation](../src/features/RedisServices/README.md)
for transaction and contribution boundaries. Choose another documented pattern
when the task needs different semantics; name it and explain the difference.

For a new tenant-owned metadata module, the [feature starter](../templates/feature/README.md)
provides a small Create/Get/Update example. `scripts/check_feature_template.py` builds and tests
its materialized output with the normal production roles, analyzers and architecture suite.
Use the real compiled references above when a capability needs different semantics; the metadata
starter does not supply workload admission or lifecycle behavior.

## Review the first slice before repeating it

Before implementing a new feature or repeated endpoint family, name the affected patterns and
their exact maintained, compiled and tested reference files. Choose references with matching
semantics; explain intentional differences against the canonical engineering and module rules.

Implement one representative endpoint with its domain operation, persistence capability and
meaningful test. The primary agent compares that completed slice with the selected references
before delegating or repeating the pattern. Record the inspected snapshot, concrete matches,
justified differences and corrections in the local ignored task notes. A list of reference paths
without the comparison does not complete this checkpoint. Correct findings before expanding the
same pattern to other operations.

Check the request/outcome boundary, possible errors, factory/default ownership,
mutable data ownership, tenant predicates, transaction/outbox boundary, and test
assertions. This is an internal review checkpoint, not another user approval gate.
Existing authorization remains valid. Ordinary isolated fixes do not require an
artificial new endpoint or a duplicated review ceremony.

## Review record and review fixes

Use the [review checklist](review-checklist.md) for self-review and independent review. Record the
base/head, actual automated evidence, manual dispositions, findings and justified exceptions.
Keep the policy in the linked engineering documents; a private history of observations is source
material, not an additional rulebook. Deferred proposals and task-specific commands do not become
requirements through repetition in notes.

Complete the checklist's [structure and pattern pass](review-checklist.md#structure-and-pattern-pass)
over the whole diff and record concrete conclusions, including supporting-feature changes.
Apply [repeated-finding review](review-checklist.md#repeated-findings) before treating a local fix
as closure of a pattern-level finding. Passing automated checks does not replace either inspection.

Fix each review finding in its own commit by default, including its needed regression tests and
documentation. Combine findings only when the user has agreed to that grouping. Do not hide
independent fixes in a generic cleanup commit. Commit authorization does not authorize push, PR
creation or merge.

User-facing review handoffs identify committed SHAs and their diff, checks and remaining risks.
Keep the task's changes committed and its working tree clean; do not discard unrelated user or
agent work to obtain that state. Self-review during implementation may inspect a working diff,
but an uncommitted patch does not replace the committed result handed to the user.

## Automated rules

The repository compiler diagnostics complement review:

- `POLE001`: raw Mongo query APIs are forbidden below any Application `Endpoints`, `Consumers`
  or `Jobs` folder. F# architecture tests use the same rule. Workflow implementations may access
  Mongo directly; the shared Application-level `Mongo` folder contains definitions rather than all database operations.
- `POLE002`: each endpoint handler owns a colocated outcome.
- `POLE003`: entities with a matching domain factory are constructed through it.
- `POLE004`: those entities do not expose mutable collection APIs.
- `POLE005`: Domain `State`, `Status` and `Lifecycle` properties use dedicated types.
- `POLE006`: one concrete endpoint-handler contract per endpoint file.
- `POLE007`: each parameter of multi-parameter positional records and record structs
  starts on its own line; architecture tests cover all sources. Unions and delegates follow
  ordinary method formatting.
- `POLE008` rejects inline Mongo filter lambdas in Application runtime operations; use named
  `*Filters` helpers. It resolves driver filter arguments semantically, so ordinary collection
  predicates remain allowed.
- `POLE009`: positional records do not add stored instance members or alternate data constructors;
  ordinary-property records, computed/static members and DI classes remain allowed. Existing unrelated
  legacy members have exact per-member allowances, checked by architecture tests.
- `POLE011`: Domain constructors require explicit arguments; factories own initial defaults.
- `POLE010`: feature-owned typed Mongo collection roots belong to the owning Domain project.
- `POLE012`: switches over closed hierarchies name their variants instead of hiding new cases
  behind catch-all arms. Open extension interfaces retain their own unsupported-input boundary.
- `POLE013`: production C# Mongo filter, update, sort and index builders use typed field expressions.
  Serializer-aware aggregation paths retain the reviewed analyzer's actual scope.
- `.editorconfig` and pinned CleanupCode verify changed C# source formatting with `Pole Safe Cleanup`.
- `POLE014`: production code uses typed MongoDB APIs instead of `BsonDocument`, `BsonArray` and
  `BsonValue`; deliberate boundaries require a reasoned `ViolateArchitecture` attribute. Migration
  and test project roles are exempt. C# compilation and semantic F# architecture checks enforce it.

Rules use compiler symbols rather than treating every `Store`, array or `Status`
identifier as a violation. Factory detection follows the existing same-namespace
`EntityFactory` convention. Read-only interfaces do not prove their backing data
is immutable; aliasing and complete state modeling still require review and tests.
Do not silence a diagnostic to pass the gate. Correct the boundary, or justify and
test a refinement to the rule when it misclassifies valid code.

## Local and CI gate profiles

Verification is selected from the **complete PR diff**, including staged, unstaged and untracked
inputs, against the merge base of the named review target. Never classify only the last commit.
Run `python3 scripts/quality_gate.py plan --base origin/master` to inspect paths, reasons and
selected lanes. Unknown or unproven changes retain behavioral verification.

| Change                  | Admission criteria                                                                         | Required evidence                                                                                                    |
| ----------------------- | ------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------- |
| Documentation           | Canonical Markdown/skills paths only; executable assets and fixtures excluded              | Changed-file formatting, local targets/anchors, skill metadata, content/command self-review                          |
| Formatting              | Existing regular files, unchanged executable mode; supported syntax/structured-value proof | Proof, current formatter, diff check and self-review; no behavioral suites                                           |
| Templates/generator     | Starter inputs or owned generator/checker and their tests                                  | Generated solution build, formatting, full architecture and generated unit/Mongo suites; automation for tool changes |
| Template formatting     | Proven unchanged generated C# tokens or project XML values                                 | Generate output and run its formatting checks; no Mongo rerun                                                        |
| Behavioral or uncertain | Runtime/build/dependencies, analyzers, gate/CI mechanisms, unsupported proofs              | All PR-profile suites; full profile for broad/infrastructure changes                                                 |

The default `python3 scripts/quality_gate.py run --profile pr` selects these obligations; mixed
changes take their union. `--profile full` explicitly forces every lane, including Kubernetes and
Aspire E2E, after `bash infrastructure/kubernetes-lab/lab.sh up`. CI uses the same selection for
PRs/master pushes; manual workflow_dispatch forces full. Unselected jobs are **not required**, not
passed tests. The stable `Required verification` CI job rejects failed, cancelled, missing or
skipped selected jobs. Configure branch protection to require that aggregate check.

Formatting proof is deliberately narrow. C# uses the pinned SDK's preview parser and compares
exact tokens plus non-whitespace trivia, retaining literals, comments, directives and inactive
branches. Syntax errors, import/member rearrangement and semantic cleanup are not formatting.
UTF-8 MSBuild/solution XML retains element order, attributes, property text, comments and processing
instructions and namespace declarations. Nested XML property/metadata values, custom task payloads,
ProjectExtensions, DTD, CDATA and xml:space are unsupported and require behavioral verification.
JSON retains lexical literals and key
order. F#, YAML, JavaScript/TypeScript/JSX, other formats, new/deleted code and renames have no
automatic formatting exemption. A whitespace-only Git diff is not a proof. Formatter/settings
changes receive normal behavioral verification, not a filename-based waiver.

Documentation classification applies to canonical docs, feature docs, module/package README files
(including frontend, shared libraries, infrastructure and SDKs) and skill Markdown,
not arbitrary `.md` fixtures/assets. Generated README text is documentation. Review changed
commands and agent authority manually; a link or frontmatter check does not validate their intent.
Kubernetes audits are required only for selected Kubernetes lanes; do not start a lab for docs or
metadata-template Mongo tests.

Complete generated evidence requires the requested mode and exactly three distinct suites
(unit, architecture, integration), with successful nonzero integer counters and no skips. The
checker validates its project inventory before building; local receipt admission and the CI job
validate the produced report independently. Formatting-only reports cannot satisfy complete mode.

Every **selected** suite runs without filters and must have zero failures/skips. Missing prerequisites,
expected failures and baseline failures block the selected lane. The `pr` behavioral profile proves
Fast, Services and frontend; it does not prove live Kubernetes/E2E behavior.

Both profiles discover projects from Pole.slnx and run restore/build once, followed by bounded
concurrent project execution with coverage. Local defaults are Fast 4, Services 2, Kubernetes 1,
E2E 1. CI defaults are 2, 1, 1, 1. Groups execute in that order, so heavy groups do not overlap.
A local ignored `.quality-gate.local.json` or explicit `--config path.json` overrides limits.
For example `{"fast": 8, "services": 2, "buildMaxCpuCount": 8, "integrationMaxParallelThreads": 4}`.
Build workers default to `"auto"` locally and 2 in CI; central Integration xUnit workers default
to 4 locally and 2 in CI. Numeric limits must be positive integers. Build scheduling remains one
MSBuild dependency graph; concurrent project tests use existing outputs without rebuilding.

After build, the standalone backend lane is
`python3 scripts/quality_gate.py backend --profile pr --results-directory .artifacts/backend-new-run`.
Each invocation needs a fresh directory. The backend manifest records profile, project inventory,
limits and per-project command durations. TRX and Cobertura checks require every selected project.
Frontend pending/todo tests also fail the gate. The full profile verifies lab readiness and leaks.
Logs and results live under `.artifacts/quality/`.

Run `dotnet tool restore` once before standalone formatting commands. CleanupCode is pinned
in `.config/dotnet-tools.json`; it requires a .NET 10 runtime alongside the pinned .NET 11 SDK.
CI installs both, and the complete local gate restores tools before formatting.

Use `python3 scripts/quality_gate.py format` to apply C# formatting before the final run. It uses `origin/master` as the comparison base by default;
`--base` can select the explicitly agreed base of a stacked change. Formatting
only changed files avoids unrelated repository-wide rewrites; each selected behavioral suite remains complete rather than filtered by changed files. For a stacked PR tested with
`run --base origin/parent`, publish with `open-pr --base parent`. The publication
command defaults to `master` and rejects a target whose origin ref differs from
the recorded review base.

`check-format` formats temporary copies and fails if any selected C# file differs; it never
rewrites sources or the Git index. `format` applies the resulting diff only after a successful
tool run, refuses partially staged files and concurrent source/settings edits, and never stages
changes. Logs, per-file differences and the pinned tool/profile metadata are retained under
`.artifacts/cleanupcode/`. CI uploads the metadata-only formatting report; source
patches and raw tool logs stay local. Tool failures print the final formatting diagnostics.

`Pole Safe Cleanup` is a shared Rider profile in `Pole.sln.DotSettings` with only
`CSReformatCode` enabled. The gate validates that contract and invokes CleanupCode on files,
without a solution: this mode only reformats and does not execute semantic cleanup tasks.
The selected source paths and their EditorConfig hierarchy are copied before the tool runs;
global, personal and automatic project settings layers are disabled. F#, project XML and
other languages are excluded from this C# lane. Keep `Pole Full Cleanup` as an explicit IDE
operation: it is not the gate profile. The broader profile previously removed required F#
imports and an object cast needed for serializer overload inference.

For a safe combined changed-file check, use
`python3 scripts/format_changed.py --base <review-base> --check`. It checks C# with
the pinned CleanupCode formatter and supported frontend/document files with the locally
installed Prettier. Use `--write` to apply formatting before review. Write mode
refuses files with both staged and unstaged edits. Neither mode stages, stashes,
downloads tools, installs hooks, or changes Git configuration. A fully staged
file's formatted working copy still needs deliberate review and staging. Project
XML and other formats without a configured formatter retain their repository
layout and are checked with `git diff --check`; the command does not reserialize
them. Run the command again with `--check` after formatting.

A failed prerequisite is a blocked gate, not a skipped passing suite. Repair the
environment or report the blocker; do not open even a draft PR while it remains.
The gate stops on failure, so a failed run may leave later suites unexecuted.
Only a subsequent complete successful run authorizes publication.

After selected verification, complete self-review and, for non-trivial code changes, a fresh
independent read-only review as required by `AGENTS.md`. For its documented
documentation/mechanical exemptions, record the primary agent’s self-review instead.
Record the actual review result:

```bash
python3 scripts/quality_gate.py record-review --agent <reviewer-id> --summary-file <review-summary>
python3 scripts/quality_gate.py verify
# Commit and push only when the user has authorized them.
python3 scripts/quality_gate.py open-pr --title '<title>' --body-file <description>
```

The wrapper also requires a clean task branch whose pushed commit matches local HEAD.
The receipt binds current structural checks, selected suites, source fingerprint, review target and
project inventory. `record-review`, `verify` and `open-pr` accept reduced evidence only when it covers
the complete current diff. They do not accept a user-selected category flag or a forged "docs" label.

After a successful **committed, clean** tested state, `run` may reuse its behavioral results if the
review target is unchanged and the entire additional diff is documentation or proven formatting.
The receipt records the previous tested SHA/fingerprint, current SHA and exact delta proofs; new
structural/generated-format checks still run and review must be recorded again. No reuse is allowed
from failed/interrupted evidence, dirty tested inputs, insufficient selected lanes, changed base,
semantic/template changes or changed project inventory. Test and gate source changes receive no
special exemption. Explicit `--profile full` always runs freshly. Uncommitted runs can be verified
unchanged but cannot seed reuse. A rebase changing the review base requires fresh evidence.

The receipt is local evidence, not a substitute for CI or required independent review. Do not
fabricate one or bypass the wrapper with direct PR creation. Publication still requires a clean
authorized task branch, matching pushed HEAD and matching target base; no gate grants Git permission.

## Diagnose before retrying a failed run

Before repeating a failed build, test, CI job or gate, inspect the first actionable failure in the
original run. Identify the check, what was expected and what actually happened. Use its report,
logs or resource events to distinguish a failed assertion from a missing prerequisite.

State the known cause or a working hypothesis and the smallest observation that would confirm
or rule it out. For an expensive or delegated run, leave this with the run or log reference in
the task notes or handoff so the next agent can continue. For example, if an Aspire test times
out waiting for a Deployment and no Pod exists, inspect Deployment and ReplicaSet events before
launching the suite again.

Retry when a fix or prerequisite has changed, or when a targeted repeat tests a specific
hypothesis such as intermittent ordering. Say what new evidence the repeat should yield.
If it produces the same failure without new evidence, inspect another signal or report the
concrete blocker. Once resolved, run the verification required for the complete diff.

## Failure-first summaries and lasting fixes

When tests are failing, the **first sentence of every summary** must name the
failing tests/suites and explain the known reason, or say that the cause is still
being investigated. When a suite cannot run, lead with that suite and its blocking
prerequisite. This applies to handoffs, progress summaries, PR descriptions and
final answers. Do not bury failures after a positive implementation summary.
After fixes, report the rerun evidence accurately; old failed results are not
current failures, and a partial rerun is not a full-suite pass.

For a repeated mistake, leave a durable safeguard at the smallest useful boundary:
a regression test, semantic diagnostic, formatter rule, or correction to the
canonical example. Do not add a broad prohibition based only on one incident.
Review consumes the exact diff and current test evidence; the primary agent owns
triage, fixes and the final claim of completion.

On an SELinux-enforcing Podman host, a new worktree can give bind-mounted
configuration files the `user_tmp_t` or `user_home_t` label. Check `getenforce` and
`ls -Z` on the actual container host: a sandbox without the SELinux filesystem can
report `Disabled` even when the host is enforcing. If container logs report permission
denied for the existing read-only Keycloak or BuildKit configuration mount, inspect
`ls -Z` and label only those files `container_file_t` with `chcon -t container_file_t`:
`infrastructure/keycloak/realms/pole.control-plane.realm.json` and
`src/api/Pole.AppHost/BuildKit/buildkitd.toml`. Do not disable SELinux or relabel the
whole repository to make tests pass.
