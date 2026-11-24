---
name: pole-flow-docs
description: Trace a Pole feature or runtime flow end to end. Use when locating ownership, explaining execution, diagnosing a bug, planning a change, or writing an evidence-based flow document.
---

# Pole Flow Tracing

Trace the shortest complete path that answers the question. Stay read-only unless the user explicitly asks to update documentation.

Follow the applicable sequence:

1. Entry point: HTTP route, hosted service, scheduled job, event consumer, or frontend action.
2. Boundary types: request body, route/header binding, DTO, contract, event, or frontend API type.
3. Validation and orchestration: validator, Mediator pipeline, handler, service, grain, or hook.
4. State: domain transition, Mongo collection/filter/transaction, cache, or frontend state.
5. Asynchronous work: outbox write, published event, consumer, retry, and idempotency boundary.
6. External effect: Kubernetes, DNS, Harbor, Git provider, RabbitMQ, Keycloak, search, or browser-visible result.
7. Completion: response mapping, persisted status, projection, or UI refresh.

Use exact routes, event and DTO names, symbols, collection or resource names, and file references. Separate observations as:

- `Verified`: directly supported by code, configuration, tests, or current runtime evidence.
- `Inferred`: the most likely behavior, with the evidence behind the inference.
- `Unknown`: the repository does not establish it.
- `Needs verification`: a concrete command or runtime observation could settle it.

Return a compact execution narrative, important branches and failure paths, transaction and tenant boundaries, external dependencies, and unanswered questions. Do not propose a fix unless the parent task asks for one.
