# Aspire Local Development

Use the Aspire AppHost when a change must be verified against Pole's real local service graph. The normal integration suite remains the default for fast, isolated validation.

## Prerequisites

- Docker is running.
- The repository's .NET SDK and Node.js dependencies are available.
- On Linux, the selected MongoDB image supports the host kernel. MongoDB 8.x currently rejects
  kernels 6.19 and later; track [SERVER-121912](https://jira.mongodb.org/browse/SERVER-121912).
- The Aspire CLI and Aspire MCP server are installed.
- A LocalKeyFile KEK has been bootstrapped and configured through AppHost user secrets; see
  [Envelope encryption operations](envelope-encryption.md#aspire-local-development).
- A Kubernetes context is configured when the workflow exercises reconciliation. The default
  deployment-target bootstrap and the core Aspire E2E resource-group flow both require one.

The AppHost is `src/api/Pole.AppHost/Pole.AppHost.csproj`. It starts MongoDB 7.0 (pinned to match
the integration-test fixture), Keycloak, RabbitMQ, the Pole API, and the Vite frontend. The sample
`blog` project requires an explicit start.

The local MongoDB resource explicitly uses plaintext transport through
`WithoutHttpsCertificate()`. Aspire 13.6 enables MongoDB TLS by default when a developer
certificate is available; Pole's custom replica-set bootstrap and Mongo Express retain
their existing local connection setup.

Pole validates and loads the complete envelope-encryption key ring before migrations or background
jobs start. A missing or invalid key is an intentional fail-closed startup error; AppHost never
creates a replacement.

Source builds add two optional, explicit-start resources:

- `pole-harbor` runs Harbor's official generated Docker Compose deployment.
- `pole-buildkit` runs pinned `moby/buildkit:v0.31.2` with its gRPC endpoint on port `1234`.

They are excluded from the default graph because Harbor requires at least 4 GB of memory and several containers. See [GitHub source build E2E](github-source-build-e2e.md) for the one-time Harbor preparation and AppHost configuration.

Keycloak's management interface uses HTTP locally for Aspire readiness checks. The browser-facing Keycloak endpoint still supports HTTPS; keeping management health on HTTP avoids Windows SChannel failures against the generated development certificate.

## Start And Inspect

1. Run Aspire's environment diagnostics before the first start.
2. Start the AppHost from the repository root:

   ```powershell
   aspire run --apphost src/api/Pole.AppHost/Pole.AppHost.csproj
   ```

3. Select this AppHost in Aspire MCP if more than one AppHost is detected.
4. Poll the resource list until required resources report a healthy running state.
5. Read resource console or structured logs when a resource is unhealthy. Summarize relevant errors; do not reproduce complete logs.
6. Obtain current endpoints from the resource list instead of assuming ports, except for the frontend's configured `http://localhost:5173` endpoint.

When testing source builds, explicitly start `pole-harbor` and `pole-buildkit` before queueing a build. The Pole API may start without them so ordinary development is not blocked by the heavier registry stack.

On Windows, stop the `pole-server` Aspire resource before building projects that replace its loaded assemblies. Restart it after the build. A running server can lock output DLLs and cause misleading build failures.

## Resetting data after a schema-breaking change

Issue #54 intentionally provides no backfill for embedded ResourceGroup resources or legacy
Storage Profile enum values. For a development environment created before that change, stop the
AppHost and remove the persistent `pole-mongo-data` Docker volume, then restart the AppHost. This
deletes all local Pole Mongo data and cannot be undone; do not use this procedure for an environment
whose data must be retained.

For an isolated, disposable AppHost graph with real Keycloak authentication and a browser smoke path, see [Aspire E2E testing](aspire-e2e-testing.md). That profile deliberately uses random host ports and session-scoped containers, rather than the persistent local-development data volumes.

## Validation Boundaries

Use the cheapest boundary that proves the behavior:

- Run `tests/Pole.Tests.Integration` for handlers, API binding, persistence, auth policies, manifests, and outbox behavior.
- Use Aspire for startup wiring, real authentication, RabbitMQ delivery, scheduled jobs, and workflows spanning several services.
- Use live Kubernetes checks only when generated manifests and in-process tests cannot prove the behavior.

An Aspire run does not replace automated tests. A green integration suite does not prove that production dependency injection, endpoint binding, authentication claim mapping, or the real MassTransit bus can start.

## Full-Stack Test Discipline

1. Create a unique test identity and unique names for every tenant and resource.
2. Record every created object so it can be removed even after a partial failure.
3. Keep raw CI deployment tokens only in process memory. Never print them in chat, logs, test output, command history, or committed files.
4. Exercise both the user-authenticated control-plane request and the service-token deployment request.
5. Verify persisted release provenance, token last-used metadata, published events, and the expected external side effect.
6. Revoke the service token and verify that reuse is rejected.
7. Clean up all test data and verify cleanup before declaring the run complete.

Do not seed synthetic outbox messages while production Hangfire workers are active unless that scheduled path is the behavior under test. Stopping RabbitMQ is insufficient isolation: Hangfire can retain work and publish it after RabbitMQ restarts. Outbox execution must remain non-concurrent, but E2E tests should still isolate their data and consumers.

## Cleanup Checklist

- Remove test web services, resource groups, tenants, users, deployment tokens, releases, and outbox records from MongoDB.
- Remove the dedicated test user from Keycloak.
- Purge test and error queues created in RabbitMQ, then verify that they are empty.
- Delete any test Kubernetes namespace and verify that it no longer exists.
- Remove transient local files generated by the run, including `aspire.config.json` when it was created only for the session.
- Do not commit package-lock or generated graph changes that contain no meaningful source change.

## Failure Triage

Investigate startup failures from the lowest dependency upward:

1. AppHost and container process errors, including executable permissions and line endings.
2. Resource references and health dependencies.
3. Pole production dependency injection and endpoint binding.
4. Authentication authority, audience, and inbound claim mapping.
5. RabbitMQ connection, MassTransit version alignment, filters, consumers, and error queues.
6. Hangfire scheduling and outbox concurrency.
7. Kubernetes reconciliation and namespace state.

After a fix, restart only the affected resource when possible, rerun the focused test, then run the complete non-Kubernetes integration suite.
