# Deployment targets

Pole stores Kubernetes cluster connections as platform-owned `DeploymentTarget` records. A target is not tenant data and is never exposed through tenant APIs. The common installation still has one target, while the same registry can hold multiple clusters for later RuntimeCell and ResourceDeployment placement.

## Model and state

Each target has a stable ID, display name, stable cluster identity, connection version, configuration revision, health and capability observations, `Enabled`, and `AcceptsWorkloads`.

- `Enabled = false` prevents Pole from creating a client for the target. Existing reconciliation reports the connection as unavailable instead of silently switching clusters.
- `AcceptsWorkloads = false` rejects new workload admission but does not prevent a client from being used to reconcile workloads already assigned to the target.
- Health is observational. A degraded observation does not become an admission latch; explicit `Enabled` and `AcceptsWorkloads` state controls admission.
- Validation observations are compare-and-set against both target revision and connection version. They do not increment the configuration revision.
- Credential rotation increments both the target revision and connection version and must resolve to the same stable cluster identity. Changing credentials cannot retarget an existing target ID.

The stable identity is derived from the `kube-system` namespace UID. Validation also records the Kubernetes server version and self-subject access results for get/create/update/patch/delete on namespaces and NetworkPolicies.

The default target is stored in the singleton `DeploymentTargetSettings` document. The local cluster has no special `IsManagementCluster` flag. RuntimeCell placement persists the selected target for each isolation scope, and ResourceDeployment persists the target selected for each adopted workload. The default is therefore not mutable through the current platform API.

## Connection kinds and security

Supported connection kinds are:

- `InCluster`: stores only the kind and version. Kubernetes supplies the mounted service-account identity at runtime.
- `Kubeconfig`: accepts one explicit context and persists a canonical, self-contained credential encrypted with the shared envelope-encryption service.
- `ServiceAccount`: accepts an HTTPS API server, embedded CA, and token. The encrypted authenticated payload includes the server, CA, and token; duplicated metadata is compared after decryption so database tampering cannot redirect a valid bearer token.

Imported kubeconfigs are treated as untrusted input. Pole rejects exec and auth-provider plugins, file references, basic authentication, impersonation, insecure TLS, HTTP API servers, incomplete credentials, and ambiguous mixed credentials. Remote clients trust only the embedded CA (PEM bundles and base64 DER are supported), verify the API-server hostname against SAN entries, require the server-authentication EKU, disable redirects and proxy use, and use bounded connect and request timeouts. They do not fall back to the host trust store or download certificate material.

API responses expose only connection kind and version. Plaintext credentials, encrypted envelopes, wrapped keys, and certificate material are not returned or logged. Remote credentials use platform-scoped associated data containing the target ID, value name, and connection version. Rewrapping is available through the bounded, optimistic-CAS platform endpoint:

`POST /api/v1/platform/encryption/deployment-target-credentials/rewrap`

## Bootstrap

After envelope-encryption initialization and database migrations, startup runs an idempotent first-target bootstrap before hosted reconcilers start.

1. If a valid default already exists, startup does nothing.
2. If targets exist but settings are missing, the oldest target is selected to repair settings.
3. If the registry is empty, Pole prepares and validates one `Local cluster` connection.
4. Target and default settings are inserted in one MongoDB transaction. Unique identity and singleton settings indexes make concurrent replicas converge on one result.
5. After that transaction, MongoDB is the source of truth. Startup configuration is not consulted to replace or rotate the stored connection.

`DeploymentTargets:Bootstrap:Mode` supports `Auto`, `InCluster`, and `Kubeconfig`. `Auto` selects `Kubeconfig` in Development and `InCluster` in every other environment. Development can set an absolute `KubeconfigPath` and optional `KubeconfigContext`; otherwise Pole reads one absolute `KUBECONFIG` file or the standard user kubeconfig. Merged `KUBECONFIG` lists are rejected so the imported credential is deterministic.

Bootstrap network sources are explicit under `DeploymentTargets:Bootstrap:WorkloadConfiguration:NetworkSources`.
`Pods` maps each provider-owned source name (for example, `public-ingress`,
`functions/gateway`, or `postgres/operator`) to a required namespace and nonempty pod-label map.
`Cidrs` maps source names to a canonical
IPv4 or IPv6 CIDR; validation rejects the unrestricted `/0` prefix, and the administrator
must limit the range to the required CNI-visible source addresses. A purpose may occur in
only one map. Invalid names, labels, CIDRs, and duplicate names fail
startup. Empty maps configure no source. For example:

```json
{
  "DeploymentTargets": {
    "Bootstrap": {
      "WorkloadConfiguration": {
        "NetworkSources": {
          "Pods": {
            "public-ingress": {
              "Namespace": "kube-system",
              "PodLabels": {
                "app.kubernetes.io/name": "traefik",
                "app.kubernetes.io/instance": "traefik-kube-system"
              }
            }
          },
          "Cidrs": {}
        }
      }
    }
  }
}
```

The platform target create/update API uses a `networkSources.entries` object keyed by source
name. Each source has a JSON `kind` discriminator: `Pod` with `namespace` and `podLabels`,
or `Cidr` with `cidr`. Pod namespace and labels form one conjunctive Kubernetes policy peer;
they are not alternate identities. CIDR sources identify addresses visible to the CNI, which
may differ from the original sender after NAT. Configure only the addresses actually required
by the cluster's routing path. Tenant available-target metadata reports readiness, not these
platform source identities.

Set `DeploymentTargets:Bootstrap:Enabled=false` only for specialized test or recovery hosts that intentionally start without a default target.

## Platform API

All deployment-target endpoints require the platform-admin role:

- `GET /api/v1/platform/deployment-targets/`
- `GET /api/v1/platform/deployment-targets/{id}`
- `POST /api/v1/platform/deployment-targets/`
- `PUT /api/v1/platform/deployment-targets/{id}`
- `PUT /api/v1/platform/deployment-targets/{id}/credentials`
- `POST /api/v1/platform/deployment-targets/{id}/validate`
- `DELETE /api/v1/platform/deployment-targets/{id}`

Configuration updates and credential rotation are revision-fenced. Duplicate cluster identities are rejected. The default target cannot be deleted.

Deletion is a retryable two-phase operation. Pole first persists `DeletionRequestedAtUtc` and turns off `AcceptsWorkloads`; this durable fence prevents any new workload or durable reference from being admitted. It then performs one indexed lookup in the central `DeploymentTargetReferences` registry and finally deletes the exact fenced version. If a reference still exists, the target remains visible as pending deletion and does not accept new workloads; remove the references and retry the same delete request.

Every feature that persists a deployment-target reference must use the central reference-admission contract:

1. In the same MongoDB transaction that creates its resource placement, call `IDeploymentTargetReferenceAdmission.RegisterAsync` with a stable owner type and owner ID.
2. In the same transaction that removes the placement, call `UnregisterAsync` with the same owner identity.

The admission service writes the target fence and central registry atomically. Those fence writes contend with target deletion on the target document. A registration committed first is visible to the deletion guard; a deletion fence committed first rejects registration. Reference removal can race safely, but may make the current delete attempt return a conflict that is resolved by retrying.

Runtime cells register `OwnerType = "RuntimeCell"` and `OwnerId = RuntimeCell.Id` in the
same transaction that creates the cell and its initial reconciliation event. The reference
remains until namespace deletion succeeds. It is a referential-integrity index used to block
unsafe target deletion; the RuntimeCell document remains the source of truth for placement
and lifecycle. See [workload placement](workload-placement.md).

ComputeProfile target mappings also register references with owner type `compute-profile` in the
same transaction as catalog changes. The reference protects the mapping; the profile remains the
source of truth. Mapping validation inspects node labels, architecture, extended-resource
allocatable quantities, and RuntimeClass scheduling against the target cluster without treating
temporary cordon/readiness state as permanent capability.

`POST /{id}/validate` is an operational health refresh, not request validation. It rebuilds a client from the stored credential, probes Kubernetes health and namespace capabilities, verifies the stable cluster identity, and compare-and-sets only the observational fields when the target revision and connection version are unchanged.

## Client lifecycle

`IKubernetesClientProvider` resolves a target from MongoDB on every request and compares durable revision and connection version with its cache. Metadata-only revisions reuse the transport; credential rotation rebuilds it. Disabled or missing targets raise a sanitized `DeploymentTargetUnavailableException`. Degraded-but-enabled targets still return a client so reconciliation can recover.

Invalidated clients are retained until provider shutdown because callers receive `IKubernetes` directly and may still be using an earlier instance. This favors no use-after-dispose over immediate socket reclamation; rotations are administrative and expected to be infrequent.

## NetworkPolicy enforcement readiness

New and bootstrap targets default to `Unknown` enforcement and cannot admit workloads.
Healthy Kubernetes API access and permission discovery do **not** prove that a CNI enforces
NetworkPolicy. Logical resource groups can still be created.

The target probe checks the platform identity's `get`, `create`, `update`, `patch`, and `delete`
authorization for cluster-scoped `namespaces` and `networking.k8s.io/networkpolicies` across
Pole-managed namespaces. Missing NetworkPolicy writer permissions mark the target degraded
and workload readiness `EnforcementRequired` with reason `WriterPermissionsMissing`. A CNI
attestation remains a separate operator decision; an RBAC check is not enforcement evidence.

A platform administrator records an operator attestation with
`PUT /api/v1/platform/deployment-targets/{id}/network-policy`:

```json
{
  "enforcement": "Supported",
  "allowIsolationNotEnforced": false,
  "revision": 1
}
```

`enforcement` accepts `Supported`, `Unsupported`, or `Unknown`. Before declaring support,
the operator must establish enforcement in that specific cluster (for example using an
independent connectivity/conformance test). Pole records `OperatorAttestation`, a safe fixed
reason, and server time; this is not an active probe. The evidence is tied to the cluster
identity and credential version. Credential rotation invalidates the attestation and override;
record a fresh attestation after validating the new connection.

The domain stores separate `UnattestedNetworkPolicy` and `AttestedNetworkPolicy` states.
An attestation requires the observed cluster identity, connection version, and check timestamp;
an unattested target does not store empty identity or version placeholders. The aggregate's
`AttestNetworkPolicy` transition binds evidence to its current connection and advances both
target and workload-configuration revisions. Tests use the same transition as the API.

Provider contracts expose typed enforcement, readiness, reason, and evidence-source values.
Their JSON representation remains the documented strings; runtime workload writers use the
contract's `AllowsWorkloads` policy rather than comparing display strings. Mongo admission uses
typed member expressions, including comparisons against the target's current cluster identity
and connection version, and is verified against the replica-set integration fixture.

The explicit platform override `allowIsolationNotEnforced: true` permits admission without
supported enforcement and reports `IsolationNotEnforced`. Without this override, unsupported
and unknown targets fail closed, including transaction admission fences and placement discovery.
Changes advance target and workload configuration revisions, so stale placement admissions
cannot silently use earlier settings.

Platform target details, tenant-safe available-target metadata, and tenant-scoped deployment
placement reads expose the current readiness. Deployment reads resolve current target metadata,
so revoking an attestation appears on the next read without waiting for workload reconciliation.
Reasons never contain provider errors, namespace names, or other tenants' workload identities.
Search `Applied` remains a workload lifecycle status; it is not evidence of isolation.

`EnforcementAttested` only describes the target's capability. RuntimeCell lifecycle creates an
ingress-only deny-all NetworkPolicy before the cell becomes Ready, and the workload writer
re-ensures that owned baseline before each apply. A failed or foreign policy keeps creation
and apply retryable or visibly conflicted. Kubernetes API acceptance is not synchronous proof
that the CNI has converged. The explicit `allowIsolationNotEnforced` override still causes Pole
to write policies but reports `IsolationNotEnforced` rather than claiming isolation. No egress
policy is installed. NetworkPolicy cannot restrict node-local traffic, and host-network or
port-forward probes do not establish ordinary pod-to-pod enforcement.

The reconciliation worker rechecks current readiness immediately before an apply, covering
requests queued before an attestation was revoked. It records retryable
`NetworkPolicyEnforcementRequired` when blocked. Delete reconciliation remains available.
This check does not actively test the data plane or revoke already-running pods. Live validation
uses reachable positive controls and denied ordinary peer pods; externally deleted or tampered
policies are repaired by bounded reconciliation, with no claim of a zero-packet repair window.
Function revision provisioning, activation, and autoscaling also use this current-readiness
check at their shared Kubernetes writer: revision apply and replica increases require evidence
or the explicit override, even when the resource deployment was previously Applied. Diagnostic
reads, deletion, and replica reductions remain available after revocation.
