---
name: pole-kubernetes-resource
description: Workflow for implementing or reviewing Kubernetes-backed resources in Pole. Use when editing RuntimeCell namespaces, ResourceDeployment placement, `ToKubernetes` mapping, Kubernetes resource managers, manifest builders, ingress/service/config/secret/statefulset/deployment generation, storage class handling, or cluster-backed tests.
---

# Pole Kubernetes Resource

Use this skill when a feature creates, updates, deletes, or validates Kubernetes resources.

## First Steps

1. Inspect the relevant feature and nearby implementation before editing.
2. Read `docs/workload-placement.md` for placement ownership and `docs/testing.md` for manifest versus live cluster testing.
3. Inspect the closest manager and manifest tests: web services, Redis, Postgres, or resource groups.

## Placement

- ResourceGroups owns RuntimeCell lifecycle; each RuntimeCell owns the namespace for one IsolationScope on one DeploymentTarget. A ResourceGroup is an organizational aggregate, not a namespace.
- Use ResourceDeployment placement, mutation, and fence capabilities in the caller-owned Mongo transaction. ResourceDeployment stores the physical placement and immutable compute snapshot; do not add namespace or target fields to logical resource aggregates.
- Resolve Kubernetes clients through the persisted deployment target and build manifests from the persisted ResourceDeployment placement. Do not substitute the default target during reconciliation or cleanup.
- Namespaces carry tenant, resource-group, isolation-scope, runtime-cell, and deployment-target ownership labels. Workload selectors additionally use stable resource and resource-deployment IDs. Names and reconciliation generations are not authorization identities.
- Verify complete ownership before replacing or deleting an existing namespace or policy; never adopt a conflicting object by overwriting its labels.

## Resource Manager Pattern

- Keep conversion from domain to Kubernetes shape in `ToKubernetes` extension methods or builder modules.
- Keep API command handlers focused on domain persistence and outbox writes.
- Let event consumers call Kubernetes resource managers after the transaction commits through the outbox.
- Make create/replace/delete operations tolerant of expected Kubernetes states where existing managers already do that.
- Use the official Kubernetes client and existing helper extensions instead of shelling out to `kubectl` from production code.

## Resource-Specific Notes

- Web services manage config maps, deployments, services, ingress, secrets, and optional autoscaling.
- Redis manages stateful sets, services, storage, and optional storage class behavior.
- Postgres supports operator and StatefulSet deployment modes; keep secret handling and connection-string behavior aligned with current F# modules.
- ResourceGroups manages RuntimeCell namespaces and their ownership labels.

## Testing

- Add manifest tests for object kind, name, namespace, labels, selectors, ports, env/config, secret references, storage, TLS, and ingress class.
- Add live Kubernetes tests for behavior that manifests cannot prove, including traffic enforcement. NetworkPolicy traffic tests need an initial successful connection and ordinary source pods; API acceptance, port-forward, and node-local traffic do not establish pod-to-pod isolation.
- Use `KubernetesIntegrationTestBase` and `KubernetesFactAttribute` patterns for cluster tests.
- Do not require Kubernetes for ordinary handler tests; use fakes for infrastructure-facing collaborators where possible.

## Validation

- Run the relevant targeted filter through `$pole-test-runner`.
- For changes that alter generated Kubernetes object shape, run the matching manifest tests even if no live cluster is available.
