# Repository GitOps examples

Each `manifests` directory is a complete desired snapshot. Copy one selected snapshot into a GitHub
repository and select that exact directory when creating a GitOps source. Pole reads `.yaml` and
`.yml` recursively; pointing a source at this examples directory would combine incompatible snapshots.

| Snapshot                            | Content                                                      | Purpose                                                                                |
| ----------------------------------- | ------------------------------------------------------------ | -------------------------------------------------------------------------------------- |
| `minimal/manifests`                 | ResourceGroup and Private WebService                         | First repository apply and same-commit no-op                                           |
| `full/manifests`                    | Fifteen declarations across thirteen core kinds              | Scopes, bootstrap/runtime grants, workloads, connectivity, domains and source delivery |
| `backups/manifests`                 | Secret metadata, BackupStorage grant, destination and policy | Backups for a separately managed PostgreSQL service                                    |
| `lifecycle/before/manifests`        | Group, Private WebService and Secret metadata                | Adoption and reviewed lifecycle starting point                                         |
| `lifecycle/after-rename/manifests`  | Same group/Secret, renamed WebService                        | Explicit replacement/rename                                                            |
| `lifecycle/after-removal/manifests` | Group only                                                   | Retain versus protected removal                                                        |
| `lifecycle/empty/manifests`         | Existing directory containing only `.keep`                   | Explicit confirmation for an empty desired set                                         |

The current schemas, identity rules and lifecycle guarantees are in the
[canonical repository contract](../docs/repository-contracts.md). These files are loaded directly by
compiler/phase-graph regression tests. Live fixtures replace only their explicitly selected catalog,
Git repository and Function image inputs.

## Prepare the platform and GitHub repositories

Use a tenant where the example technical names are available. An existing resource with the same
kind/name requires an adoption or replacement decision; creating a source never silently takes it over.

1. Create an admitted DeploymentTarget with technical name `demo-target`. The full snapshot needs
   managed public ingress, a public base domain and Function gateway network-source admission.
2. Create ComputeProfile `demo-compute` with revision `1`, and allow the selected tenant, target and
   workload kinds. The full snapshot additionally needs StorageProfile `demo-storage`, revision `1`,
   with an available storage class and current policy allowing 1 GiB PostgreSQL and Redis volumes.
   Historical revisions supply pinned configuration; current access and size restrictions still apply.
3. Connect a GitHub App installation with technical name `github-demo` through Integrations. Grant
   it access to the manifest repository and, for the full snapshot, the application repository.
4. Copy the chosen `manifests` directory into the manifest repository, or keep its complete example
   path. Commit and push the files to the branch that the source will observe.

For the full snapshot, edit these application-specific inputs before the first synchronization:

- `source-repository.yaml`: replace external GitHub `repositoryId: '123456789'` and branch `main`
  with the application repository identity and branch. This ID is GitHub's external repository ID;
  the YAML contains no internal Pole resource ID.
- `function-app.yaml`: replace the `registry.example.test/pole/functions:replace-with-sdk-image`
  image with your published application built using the [Pole Functions SDK](https://github.com/vpole-cloud/vpole-functions).
  Register its `hello` HTTP handler and arrange server-owned registry access where required.
- `domain-zone.yaml`: choose an available managed child-zone label if `gitops-mixed` is reserved.
  The target's public ingress and managed-domain policy must permit the binding.

The full example's application recipe publishes `traefik/whoami:v1.11.0` with a repository revision
file at `/app/revision.txt`. Add a tracked `src/Program.cs` file to that application repository so
its root build context contains the Dockerfile's `COPY` input. The file can contain demonstration
text. To build your own application, replace the inline Dockerfile with its executable build recipe.
The analyzer in the existing WebService authoring flow can help prepare a recipe; GitOps never runs it.

## Create a source and inspect execution

Open **GitOps → New source**. Select the connected GitHub installation, manifest repository and
branch. Enter the exact manifest directory, such as `examples/minimal/manifests` when using this
repository's layout. A blank directory selects the repository root. Keep polling at 300 seconds and
prune at `Retain` for the first pass.

The source is saved before asynchronous execution completes. Overview shows requested/completed
generations and observed, attempted and fully applied commits. Runs shows phase operations, changed
field paths and sanitized blockers. Configuration applied is separate from native runtime readiness.
Sync reads the current branch head; Retry repeats the pinned input of an eligible failed/blocked run.

The minimal snapshot creates the default group scope and one Private WebService. After the first
successful apply, Sync without changing Git should record a verified no-op. Git-owned configuration
is changed through Git; existing resource views retain their supported operational actions.

## Supply Secret values outside Git

The full snapshot creates Secret metadata named `gitops-mixed-auth`, with logical key `password`.
Prepare can complete while Configure waits for the value. Use the existing protected Secrets UI/API
to supply the complete value map. Store the value in an appropriate password manager and enter it
outside the repository. Do not add values, credentials or native Kubernetes Secret names to YAML.

The explicit grants have different effects:

- `mixed-web-binding` and `mixed-function-binding` authorize runtime Secret projection.
- `mixed-bootstrap-grant` authorizes PostgreSQL's initial password only; it creates no runtime
  projection and does not keep the source value as a password-rotation dependency.

Providing the missing value triggers dependency recovery without a new Git commit. Existing
PostgreSQL adoption retains its credentials and does not bootstrap again. After initial creation,
rotate PostgreSQL credentials through its current operational workflow and restrictions. Function
and WebService rollout confirmation controls retention of previous Secret projections.

## Build the first WebService artifact

The full WebService delegates image ownership to `mixed-build`. GitOps can finish configuration in
`AwaitingFirstArtifact`; placements and HTTP routes remain inert until an artifact is available.

Open that WebService's Builds section and explicitly select **Build**. The job resolves the current
application branch once, records its immutable SHA and Dockerfile/configuration input, then publishes
and promotes an immutable digest through the existing build workflow. That application SHA is
independent of the manifest commit. A newer build generation or changed association prevents a late
older job from overwriting the current artifact.

GitOps polling, Sync, webhook delivery and recipe changes never enqueue a build. Later GitOps
reconciliation preserves the delivered digest while managing the declared image-authority association
and other configuration. A new manual Build reads the application branch again.

## Verify reconciliation

Commit a permitted managed-field change, such as WebService tags, then Sync and inspect the update
operation. A following Sync should show no-op. Polling also checks the existing managed fields at an
unchanged manifest SHA. Runtime observations, autoscaler output, generated Secret projections and
delivery digests are outside managed configuration.

Production APIs reject manual configuration writes to Git-owned resources. Automated drift tests use
an isolated Mongo fixture to inject controlled changes; no production drift-test endpoint exists.
A missing previously created identity is a blocker and is never recreated or rebound by its name.

Managed hostname attachment and ingress rules describe desired routing. Check native readiness and
routed HTTP independently. Local ingress/network assertions do not establish public DNS or TLS.

## Reviewed lifecycle walkthrough

Use a separate source for the lifecycle snapshots. Initially copy `lifecycle/before/manifests` into
its configured manifest directory. Keep the directory stable while publishing the later snapshots.
Pause is useful while authoring a concrete plan; it retains ownership and manual-write guards.
Explicit reviewed lifecycle commands remain available while paused.

### Adopt existing resources

Create compatible manual resources with the `lifecycle-group`, `lifecycle-api` and `lifecycle-auth`
names, or use an isolated tenant containing those resources. Publish the matching `before` snapshot.

In Previews, select adoption candidates by kind/name. The server returns exact identities and
ownership metadata; the preview performs compatibility and dependency checks. For an unnamed manual
SecretBinding, select its Secret and consumer, then enter the desired declaration name from YAML.
Generated/default entities and resources owned by another source cannot be adopted independently.

Review the concrete actions and field paths before Confirm. Adoption preserves resource IDs, Secret
values, PostgreSQL credentials, delivered artifacts and unmanaged fields. Adopting a parent does not
implicitly transfer its children. A partial failure remains visible; ownership is not silently undone.

### Rename explicitly

Replace the contents of the source's configured directory with the `after-rename` snapshot and
commit it. Select the existing `lifecycle-api` inventory identity and the desired WebService name
`lifecycle-renamed-api` in a replacement preview. The server reserves new IDs and includes required
same-source dependent changes. Manual or other-source dependencies can block the operation.

Rename uses the current delete → create workflows. Review identity loss, possible downtime, data-loss
and provider retention warnings. Confirm the stored preview, then follow pending cleanup and creation
steps to completion. Resource IDs are not preserved by rename/replacement; data migration is not added.

### Retain or remove omitted declarations

Publish `after-removal` at the same configured directory. Under `Retain`, the omitted WebService and
Secret stay owned and pending removal. The Resources section exposes their exact identities.

For deletion, create a removal/resource-changes preview and inspect the complete action set. Selecting
`Prune` in Settings does not confirm protected deletion: Secret, Redis and PostgreSQL removal always
requires explicit confirmation. An empty desired set and ambiguous same-kind create/removal also
require a preview. The `empty` snapshot demonstrates the existing-directory case; a missing directory
is an error and grants no deletion authority.

Load and review all preview pages. A plan that can lose data requires the explicit acknowledgment
checkbox before Confirm. Changing source/inventory/configuration or dependencies can make a preview
stale; build and review a new one instead of retrying old authority. Only receipt-proven effects of
that exact plan may advance its approved observations during recovery.

Deletion requests remain pending until owner cleanup finishes. Ownership is released only after that
finalization. Redis/PVC, PostgreSQL mode and storage reclaim policy determine retention behavior;
a replacement Secret requires newly supplied values. No distributed rollback is promised.

### Relocate repository input

Copy the current desired snapshot to another accessible repository, branch or directory. Create a
relocation preview selecting that destination. Review its immutable commit and consequences for the
whole inventory. Matching addresses retain IDs; removals and replacements stay within the confirmed
server plan. Relocation changes source location, not workload placement.

### Pause, resume, detach and delete

Pause fences further GitOps mutations and interrupts active execution while already accepted runtime
cleanup continues. Resume schedules a fresh Sync; an interrupted dangerous plan needs a new approval.

Detach uses an inventory-only preview and works when Git is inaccessible or the first fetch never
succeeded. After all ownership is durably released, the source becomes terminal `Detached` and manual
control returns. Resource configuration, credentials, placements and native workloads are retained.
Re-entering Git control requires a new source with explicit adoption.

Delete the source only when ownership and pending work are empty. The tombstone retains audit and
idempotency, and an old creation key cannot recreate it. Source deletion does not delete workloads.

## Backups for an independently managed service

`backups/manifests` assumes an existing PostgreSQL resource named `database` in resource group
`postgres-group`, isolation scope `private-data`, in the selected tenant. The service may be manual
or belong to another source. Keep its existing ownership. Use a separate GitOps source pointed only
at this snapshot and replace the S3 endpoint, bucket and prefix with the tenant's destination.
The endpoint must be reachable from Pole and the workload target.

The snapshot creates `storage-keys` metadata with `access`/`secret` keys; provide their values through
Secrets outside Git. `archives-access` grants exactly those keys to destination `archives` with
`usage: BackupStorage`. Policy `nightly` uses UTC cron and a seven-day PostgreSQL retention window.
After destination/policy configuration applies, CNPG/Barman own scheduled copies and WAL. Browse the
live catalog and request manual backups or a copy/time restore through the ordinary Backups UI/API.
The compiler and cross-source backup integration tests load these exact files.
