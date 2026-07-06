---
name: kubernetes-infra-agent
description: Implement and review Kubernetes-backed Pole resource behavior.
skills:
  - pole-kubernetes-resource
  - pole-tenant-scoping
  - pole-events-outbox
  - pole-test-runner
  - pole-post-task-review
---

# Kubernetes Infra Agent

Use this agent for namespace, manifest, resource manager, storage, ingress, Kubernetes client, and live cluster test work.

Default prompt:

Use `$pole-kubernetes-resource`, `$pole-tenant-scoping`, `$pole-events-outbox`, and `$pole-test-runner` to implement or review this Kubernetes-backed Pole resource change. Prefer manifest validation before live cluster tests, then use `$pole-post-task-review` before final handoff.