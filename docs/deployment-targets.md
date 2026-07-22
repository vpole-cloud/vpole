# Deployment targets

Pole stores Kubernetes cluster connections as platform-owned `DeploymentTarget` records. A target is not tenant data and is never exposed through tenant APIs. The common installation still has one target, while the same registry can hold multiple clusters for later RuntimeCell and ResourceDeployment placement.

## Model and state

Each target has a stable ID, display name, stable cluster identity, connection version, configuration revision, health and capability observations, `Enabled`, and `AcceptsWorkloads`.

- `Enabled = false` prevents Pole from creating a client for the target. Existing reconciliation reports the connection as unavailable instead of silently switching clusters.
- `AcceptsWorkloads = false` rejects new workload admission but does not prevent a client from being used to reconcile workloads already assigned to the target.
- Health is observational. A degraded observation does not become an admission latch; explicit `Enabled` and `AcceptsWorkloads` state controls admission.
- Validation observations are compare-and-set against both target revision and connection version. They do not increment the configuration revision.
- Credential rotation increments both the target revision and connection version and must resolve to the same stable cluster identity. Changing credentials cannot retarget an existing target ID.

The stable identity is derived from the `kube-system` namespace UID. Validation also records the Kubernetes server version and self-subject access results for get/create/update/patch/delete on namespaces.

The default target is stored in the singleton `DeploymentTargetSettings` document. The local cluster has no special `IsManagementCluster` flag. In the current transitional model resources without persisted placement resolve the default target. Explicit per-resource placement is introduced by the RuntimeCell and ResourceDeployment work; the default is therefore not mutable through the #35 API.

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

Deletion is a retryable two-phase operation. Pole first persists `DeletionRequestedAtUtc` and turns off `AcceptsWorkloads`; this durable fence prevents any new workload or durable reference from being admitted. It then calls every registered `IDeploymentTargetReferenceSource`, so RuntimeCell and ResourceDeployment owners can veto deletion while references exist, and finally deletes the exact fenced version. If a reference still exists, the target remains visible as pending deletion and does not accept new workloads; remove the references and retry the same delete request.

Every feature that persists a deployment-target reference must follow both sides of the reference contract:

1. Implement `IDeploymentTargetReferenceSource` and query its durable references during deletion.
2. In the same MongoDB transaction that creates the reference, call `IDeploymentTargetReferenceAdmission.FenceCreationAsync` before inserting it.
3. In the same transaction that removes the reference, call `FenceRemovalAsync` before deleting it.

Those fence writes contend with target deletion on the target document. A creation committed first is observed by the reference source; a deletion fence committed first rejects the creation. Reference removal can race safely, but may make the current delete attempt return a conflict that is resolved by retrying.

## Client lifecycle

`IKubernetesClientProvider` resolves a target from MongoDB on every request and compares durable revision and connection version with its cache. Metadata-only revisions reuse the transport; credential rotation rebuilds it. Disabled or missing targets raise a sanitized `DeploymentTargetUnavailableException`. Degraded-but-enabled targets still return a client so reconciliation can recover.

Invalidated clients are retained until provider shutdown because callers receive `IKubernetes` directly and may still be using an earlier instance. This favors no use-after-dispose over immediate socket reclamation; rotations are administrative and expected to be infrequent.
