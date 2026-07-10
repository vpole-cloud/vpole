---
name: pole-aspire-local
description: Run and troubleshoot Pole's full local service graph with Aspire. Use when starting the AppHost, inspecting Aspire resources or logs, validating production dependency injection and endpoint startup, exercising real Keycloak or RabbitMQ flows, running a service-token deployment end to end, or cleaning up full-stack test data.
---

# Pole Aspire Local

Use `docs/aspire-local-development.md` as the canonical procedure. Read it before starting or testing the stack.

## Workflow

1. Inspect the relevant feature and nearby implementation before editing.
2. Run Aspire diagnostics, list detected AppHosts, and select `src/api/Pole.AppHost/Pole.AppHost.csproj` when needed.
3. Start the AppHost with `aspire run --project src/api/Pole.AppHost/Pole.AppHost.csproj` when it is not already running.
4. Poll resources until required dependencies and `pole-server` are healthy. Inspect focused console or structured logs for unhealthy resources.
5. Stop `pole-server` before rebuilding on Windows, then restart it after the build.
6. Run the smallest automated test boundary first. Use Aspire only for behavior that requires the real service graph.
7. Use unique E2E identities and resource names. Never display a raw deployment token.
8. Verify persisted state, events, external effects, revocation, and rejected token reuse.
9. Clean MongoDB, Keycloak, RabbitMQ, and Kubernetes artifacts and verify cleanup.
10. Run the full non-Kubernetes integration suite before handoff.

## Guardrails

- Treat `docs/aspire-local-development.md` as the source of truth and update it when the workflow changes.
- Do not assume that stopping RabbitMQ isolates outbox tests; active Hangfire jobs can publish retained work after restart.
- Do not seed production outbox records unless the scheduler and consumers are part of the test and are isolated.
- Stop only the affected Aspire resource when possible; do not restart the complete stack reflexively.
- Report concise error evidence and resource state, not complete logs or secrets.
- Remove transient `aspire.config.json`, empty package-lock diffs, and unrelated generated noise before committing.
