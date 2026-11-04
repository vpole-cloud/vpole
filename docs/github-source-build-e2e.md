# GitHub Source Build E2E

This procedure verifies the source-build workflow against GitHub, Harbor, BuildKit, MongoDB, RabbitMQ, and Kubernetes. The corresponding automated, deterministic lane is documented in [Aspire E2E testing](aspire-e2e-testing.md); this guide remains useful for manual validation against a real GitHub installation and a runnable application image.

## One-time local infrastructure

Pole uses one Harbor instance per environment and creates one private Harbor project per Pole tenant. Local development retains Harbor's official multi-container topology instead of translating or maintaining it in AppHost.

1. Download the official Harbor `v2.14.4` online installer from the Harbor GitHub release.
2. Extract it to a stable local directory outside the repository.
3. Copy `harbor.yml.tmpl` to `harbor.yml` and configure at least:

   ```yaml
   hostname: host.docker.internal
   http:
     port: 5000
   external_url: http://host.docker.internal:5000
   harbor_admin_password: <local-admin-password>
   ```

4. Keep HTTPS disabled only for this local deployment. Run the installer's `prepare` command from Linux or WSL to generate `docker-compose.yml`. Do not run `install.sh`; Aspire will own `docker compose up` after preparation.
5. Install the `buildctl` client from the official BuildKit `v0.31.2` release and put it on `PATH`, or configure its absolute path below.
6. Configure Docker Desktop or the local Kubernetes container runtime to allow the development registry `host.docker.internal:5000`. AppHost's checked-in `buildkitd.toml` already marks this endpoint as HTTP/insecure for BuildKit only.

Set AppHost user secrets:

```powershell
dotnet user-secrets set "LocalSourceBuilds:Enabled" "true" --project src/api/Pole.AppHost
dotnet user-secrets set "LocalSourceBuilds:HarborComposeDirectory" "C:\path\to\harbor" --project src/api/Pole.AppHost
dotnet user-secrets set "LocalSourceBuilds:BuildctlPath" "C:\path\to\buildctl.exe" --project src/api/Pole.AppHost
dotnet user-secrets set "Parameters:harbor-admin-password" "<local-admin-password>" --project src/api/Pole.AppHost
```

For a Podman-managed Harbor Compose deployment, also set
`LocalSourceBuilds:ComposeCli` to `podman`; Docker remains the default.

The Harbor password must match `harbor.yml`. The defaults are:

- Harbor API: `http://localhost:5000`
- Registry reference used by BuildKit and Kubernetes: `host.docker.internal:5000`
- BuildKit: `tcp://localhost:1234`

Override `LocalSourceBuilds:HarborBaseUrl`, `HarborRegistryHost`, or `BuildKitAddress` only when the local topology differs. If the registry host changes, update `src/api/Pole.AppHost/BuildKit/buildkitd.toml` to match it.

## Start and verify dependencies

1. Start the AppHost and inspect resources:

   ```powershell
   aspire run --apphost src/api/Pole.AppHost/Pole.AppHost.csproj
   ```

2. Explicitly start `pole-harbor` and `pole-buildkit` in the Aspire dashboard.
3. Start or restart `pole-server` after both resources are running.
4. Verify Harbor health without printing credentials:

   ```powershell
   Invoke-RestMethod http://localhost:5000/api/v2.0/health
   buildctl --addr tcp://localhost:1234 debug workers
   ```

5. Use the automated full Kubernetes lab described in `docs/testing.md` for the repeatable Harbor
   contract and source-build E2E tests. This Compose setup is only for manual validation against a
   real GitHub installation.

## End-to-end workflow

Use unique tenant, WebService, resource-group, repository and build-configuration technical names.
Record each created ID for cleanup. This procedure describes expected behavior; it is not evidence
that the current checkout has passed the real-infrastructure checks.

1. Open `http://localhost:5173`, sign in, and select the test tenant.
2. Open **Integrations** and connect the GitHub App installation with an immutable technical name.
   Public and private repositories both use this connection.
3. Create a resource group, then open **New service → Git repository**. Select the application
   repository and branch, and give the linked repository a technical name. Linking validates access
   and the ref; it does not select or retain a build candidate.
4. Optionally run **Analyze branch** and select a suggested build plan to populate the executable
   Dockerfile. Inspect or edit that Dockerfile, or author it directly without analysis. The build
   context is the application repository root. The linked repository owns its location; the build
   configuration owns the recipe. Choose **Use this source** to save the named repository.
5. Supply the build configuration's technical name, Dockerfile and WebService configuration. Use a
   runnable recipe and matching ports/probes. Choose **Create service and build**. This creates an
   inert service without a placeholder image, binds its build configuration, accepts the workload
   configuration as `AwaitingFirstArtifact`, then explicitly queues the first Build.
6. Observe the job phases `queued`, `cloning`, `analyzing`, `building`, `pushing`, and `deploying`,
   followed by `succeeded`. The `analyzing` phase prepares the already accepted executable recipe;
   it does not choose a new analyzer candidate or replace the job's recipe.
7. In Harbor, verify a private Pole tenant project exists, the artifact digest matches Pole's job
   result, and separate build/push and runtime/pull robots exist.
8. In Kubernetes, verify the workload starts only after promotion, references the Harbor image by
   immutable digest and has its generated `pole-registry-*` image pull secret.
9. Verify the WebService release has `SourceBuild` action and provenance containing build job ID,
   source repository ID, application commit SHA, image digest and new image reference. The first
   artifact has no previous image reference; later promotions record the previous image.
10. Advance the application branch, then choose **Build and deploy** again without reanalysis.
    Verify the new job reads the current branch while the first job retains its original commit,
    Dockerfile and configuration revision. This is a new Build, not exact-input Retry.
11. Edit the recipe and save it. Confirm a new configuration revision is recorded without creating
    a job; only another explicit Build uses that recipe. Recovery of an existing job keeps its
    immutable input, even if the branch or configuration has since changed.

To inspect the waiting state before any build, use the API sequence separately: create the
WebService with `creationMode: "SourceBuild"` and `spec.image: null`, create its named build
configuration with `sourceRepositoryId`, `webServiceId` and `recipe: { dockerfile }`, then read back
`AwaitingFirstArtifact`. Before posting to the configuration's `/builds` endpoint, assert there is
no release, workload or HTTP route. Normal image-mode creation does not opt a service into source
build ownership; attaching a build configuration must not silently switch an existing image owner.

For a Git-owned service, declare `image: { sourceBuildConfiguration: <technical-name> }` and the
corresponding repository/configuration. GitOps apply may complete in `AwaitingFirstArtifact`.
Polling, Sync and GitHub webhook delivery do not queue application builds. After a manual Build,
verify a same-SHA GitOps run preserves the promoted digest and reports a no-op when the other
managed fields are unchanged. The application commit is independent of the manifest commit.
See the [canonical repository contract](../src/features/GitOps/docs/repository-contracts.md).

## Failure checks

Run at least one failure case per implementation change:

- Make only the test's BuildKit access unavailable before queueing a build and verify the job
  becomes `failed` with sanitized status. Restore access in cleanup; do not stop a shared lab
  BuildKit deployment. A failure before the first artifact leaves the service waiting; a later
  failure preserves its previous artifact.
- Remove test-local Harbor access and verify a provider-safe failure without credentials in output.
- Revoke the Git connection before execution or promotion and verify current access is rechecked.
- Advance the configuration or request a newer build before an older job finishes. The stale job
  must not overwrite the newer generation or deploy against a changed binding.
- Attempt to delete a repository with a build configuration, or a configuration still referenced by
  WebService image authority. Both are blocked; an idle build queue does not release image ownership.
- Verify another tenant cannot read the connection, repository, configuration or build history.

## Cleanup

1. Delete the test WebService and wait for its deletion to finish.
2. Remove any remaining build configuration, then unlink the source repository. Remove dependent
   test connections/bindings as required before deleting the resource group and waiting for namespace
   cleanup. The configuration cannot be removed first while the service still references it.
3. For Git-owned resources, preserve the production ownership guards. Use the isolated fixture's
   teardown for automated runs; do not bypass those guards through ad hoc database deletes. Source
   detach/delete belongs to the later lifecycle stage.
4. Remove only test-owned Harbor projects or namespaces left by an interrupted run after verifying
   ownership labels and IDs. Follow the [Aspire cleanup guide](aspire-e2e-testing.md#parallelism-and-cleanup).
5. Stop manually started `pole-harbor` and `pole-buildkit` from Aspire when no longer needed. Shared
   lab services remain under the lab's lifecycle management.
6. Confirm no credentials, generated Harbor configuration or local AppHost settings are tracked by Git.

Record actual test commands, pass/fail/skip counts and unavailable prerequisites with the delivery.
The Stage 6 Aspire/BuildKit/Kubernetes scenarios remain pending until executed against the final
integrated code; successful compilation or stub tests do not satisfy that requirement.
