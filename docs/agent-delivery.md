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
| Named persistence capability, internal Mongo implementation, atomic outbox           | [PersistCreatedRedis.cs](../src/features/RedisServices/RedisServices.Application/Persistence/Mutations/PersistCreatedRedis.cs)                                                        | `Create_Succeeds_WithValidRequest` and `PersistCreate_AbortsRedisAndOutbox_WhenTransactionFails` in RedisIntegrationTests                                                                                                   |
| Factory, immutable collections, explicit lifecycle transitions                       | [Redis.cs](../src/features/RedisServices/RedisServices.Domain/Redis.cs)                                                                                                               | `PersistUpdate_RejectsADeletingReplacement` and update/deletion cases in RedisIntegrationTests                                                                                                                              |
| Mongo session ownership and explicit commit/abort                                    | [MongoTransactionDecision.cs](../src/shared/Database/MongoTransactionDecision.cs) and [MongoTransactionPersistence.cs](../src/shared/Database/MongoTransactionPersistence.cs)         | [ClientSessionHandleExtensionsTests.cs](../tests/Pole.Tests.Integration/Database/ClientSessionHandleExtensionsTests.cs) and [OutboxPersistenceTests.cs](../tests/Pole.Tests.Integration/Database/OutboxPersistenceTests.cs) |

These are real compiled and tested source files, not copied sample implementations
that can drift. Read the owning [Redis documentation](../src/features/RedisServices/README.md)
for transaction and contribution boundaries. Choose another documented pattern
when the task needs different semantics; name it and explain the difference.

## Review the first slice before repeating it

For a new feature or repeated endpoint family, implement one representative
endpoint with its domain operation, persistence capability and meaningful test.
The primary agent inspects that slice before delegating or repeating the pattern.
Record the review and selected references in the local ignored task notes.

Check the request/outcome boundary, possible errors, factory/default ownership,
mutable data ownership, tenant predicates, transaction/outbox boundary, and test
assertions. This is an internal review checkpoint, not another user approval gate.
Existing authorization remains valid. Ordinary isolated fixes do not require an
artificial new endpoint or a duplicated review ceremony.

## Automated rules

The repository compiler diagnostics complement review:

- `POLE001`: raw Mongo query APIs are forbidden below any Application `Endpoints`, `Consumers`
  or `Jobs` folder. F# architecture tests use the same rule. Workflow implementations may access
  Mongo directly; no Mongo folder is required.
- `POLE002`: each endpoint handler owns a colocated outcome.
- `POLE003`: entities with a matching domain factory are constructed through it.
- `POLE004`: those entities do not expose mutable collection APIs.
- `POLE005`: Domain `State`, `Status` and `Lifecycle` properties use dedicated types.
- `POLE006`: one concrete endpoint-handler contract per endpoint file.
- `POLE007`: each parameter of multi-parameter positional records, record structs,
  unions and delegates starts on its own line; architecture tests cover all sources.
- `POLE008` rejects inline Mongo filter lambdas in Application runtime operations; use named
  `*Filters` helpers. It resolves driver filter arguments semantically, so ordinary collection
  predicates remain allowed.
- `POLE009`: positional records do not add stored instance members or alternate data constructors;
  ordinary-property records, computed/static members and DI classes remain allowed. Existing unrelated
  legacy members have exact per-member allowances, checked by architecture tests.
- `POLE011`: Domain constructors require explicit arguments; factories own initial defaults.
- `POLE010`: feature-owned typed Mongo collection roots belong to the owning Domain project.
- `.editorconfig` and `dotnet format whitespace` verify changed C# source formatting.

Rules use compiler symbols rather than treating every `Store`, array or `Status`
identifier as a violation. Factory detection follows the existing same-namespace
`EntityFactory` convention. Read-only interfaces do not prove their backing data
is immutable; aliasing and complete state modeling still require review and tests.
Do not silence a diagnostic to pass the gate. Correct the boundary, or justify and
test a refinement to the rule when it misclassifies valid code.

## Full local gate before a pull request

Every local test must pass on the final source tree before opening a PR. This
applies to documentation and tooling changes too. No filtered suites, skipped
tests, expected failures, unavailable infrastructure exceptions, or inherited
baseline failures qualify as success. Focused checks are useful during development
but do not replace this publication requirement.

Start or resume the existing full lab explicitly, then run:

```bash
bash infrastructure/kubernetes-lab/lab.sh up
python3 scripts/quality_gate.py run
```

The gate discovers all backend test projects, requires each in the solution,
restores/builds the solution, and runs every project sequentially without filters using the same
coverage-enabled backend executor as CI. The standalone lane is
`python3 scripts/quality_gate.py backend --results-directory .artifacts/backend-new-run` after
build/lab preparation; it requires a fresh results directory. Focused `dotnet test` remains valid
without coverage during development. The complete gate requires each project's current Cobertura
output and records its command/outcome in a backend manifest.
It verifies TRX totals and rejects skipped, failed, empty or aborted runs. It also
runs every automation test, lab recovery regression test, frontend test, frontend
build/lint, changed-file formatting and the resource leak audit. Frontend pending
and todo tests fail the gate. Logs and results are under `.artifacts/quality/`.

Use `python3 scripts/quality_gate.py format` to apply C# whitespace formatting
before the final run. It uses `origin/master` as the comparison base by default;
`--base` can select the explicitly agreed base of a stacked change. Formatting
only changed files avoids unrelated repository-wide rewrites; tests are never
restricted to changed files or features. For a stacked PR tested with
`run --base origin/parent`, publish with `open-pr --base parent`. The publication
command defaults to `master` and rejects a target whose origin ref differs from
the recorded review base.

For a safe combined changed-file check, use
`python3 scripts/format_changed.py --base <review-base> --check`. It checks C# with
the pinned SDK formatter and supported frontend/document files with the locally
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

After the full run, complete self-review and, for non-trivial code changes, a fresh
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
The receipt binds results to source contents, the test-project inventory and the
review base. Source changes invalidate it, including test-only and formatting
changes. Staging deletions/renames and committing identical tested contents do not
invalidate it. An interrupted run invalidates previous success. The receipt is local evidence, not a substitute
for CI or independent review; do not fabricate one or bypass the wrapper with a
direct PR-creation tool. The wrapper itself does not grant publication permission.

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
