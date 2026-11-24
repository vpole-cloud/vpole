---
name: pole-aspire-local
description: Run and troubleshoot Pole's Aspire service graph when validation requires real startup wiring or cross-service behavior.
---

# Pole Aspire Local

Use `docs/aspire-local-development.md` as the canonical procedure. Read it before starting or testing the stack.

## Workflow

1. Run `aspire doctor` when the environment is uncertain and `aspire ls` or `aspire ps` to discover AppHosts.
2. For agent-driven work, start Pole in the background with `aspire start --apphost src/api/Pole.AppHost/Pole.AppHost.csproj`. Use `aspire run --apphost ...` only when an attached interactive process is useful.
3. Use `aspire describe`, `aspire wait <resource>`, `aspire logs <resource>`, and `aspire otel` to inspect state without guessing ports or reproducing complete logs.
4. Rebuild or restart only the affected resource when the graph is unchanged. Restart the AppHost when its resource graph changes.
5. Run the smallest automated boundary first; use Aspire only for production composition, real authentication or messaging, scheduled jobs, browser flows, or other cross-service behavior.
6. Use unique E2E identities and resource names, verify persisted and external effects, and clean every MongoDB, Keycloak, RabbitMQ, and Kubernetes artifact created by the run.

## Guardrails

- Treat `docs/aspire-local-development.md` as the source of truth. If the installed CLI differs, inspect `aspire <command> --help` and update the documentation rather than guessing.
- Do not assume that stopping RabbitMQ isolates outbox tests; active Hangfire jobs can publish retained work after restart.
- Do not seed production outbox records unless the scheduler and consumers are part of the test and are isolated.
- Never expose deployment tokens, credentials, private keys, or complete secret-bearing logs.
- Report concise error evidence and resource state, not complete logs or secrets.
