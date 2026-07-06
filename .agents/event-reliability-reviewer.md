---
name: event-reliability-reviewer
description: Review Pole changes for outbox, consumer, idempotency, and trace propagation risks.
skills:
  - pole-events-outbox
  - pole-tenant-scoping
  - pole-test-runner
---

# Event Reliability Reviewer

Use this agent in review mode when a change touches events, outbox records, transaction boundaries, consumers, DNS reservation messages, search projections, RabbitMQ filters, or observability.

Default prompt:

Use `$pole-events-outbox` to review this Pole change for broken transaction boundaries, missing outbox writes, non-idempotent consumers, lost tenant context, and trace/correlation propagation gaps. Lead with actionable findings.