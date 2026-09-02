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
5. Install the `buildctl` client from the official BuildKit `v0.30.0` release and put it on `PATH`, or configure its absolute path below.
6. Configure Docker Desktop or the local Kubernetes container runtime to allow the development registry `host.docker.internal:5000`. AppHost's checked-in `buildkitd.toml` already marks this endpoint as HTTP/insecure for BuildKit only.

Set AppHost user secrets:

```powershell
dotnet user-secrets set "LocalSourceBuilds:Enabled" "true" --project src/api/Pole.AppHost
dotnet user-secrets set "LocalSourceBuilds:HarborComposeDirectory" "C:\path\to\harbor" --project src/api/Pole.AppHost
dotnet user-secrets set "LocalSourceBuilds:BuildctlPath" "C:\path\to\buildctl.exe" --project src/api/Pole.AppHost
dotnet user-secrets set "Parameters:harbor-admin-password" "<local-admin-password>" --project src/api/Pole.AppHost
```

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

5. Run the real Harbor contract test using the environment variables documented in `docs/testing.md`.

## End-to-end workflow

Use unique tenant, WebService, and resource-group names. Record each created id for cleanup.

1. Open `http://localhost:5173`, sign in, and select the test tenant.
2. Open **Integrations** and connect the GitHub App installation if it is not already connected.
3. Select `lsbV/pole-dotnet-sample`, its test branch, and run analysis.
4. Confirm the analysis records the selected branch's exact commit SHA and produces a .NET candidate with a read-only Dockerfile preview.
5. Link that candidate. Reanalyze once and verify the linked repository advances only to the branch's current commit.
6. Create a resource group and WebService. Its initial image is only a bootstrap image and will be replaced by the source build.
7. Select the WebService. In **Source builds**, choose the linked repository and create the configuration.
8. Choose **Build and deploy**. Observe the phases `queued`, `cloning`, `analyzing`, `building`, `pushing`, and `deploying`, followed by `succeeded`.
9. In Harbor, verify a private Pole tenant project exists, the pushed artifact has the digest displayed by Pole, and separate build/push and runtime/pull robots exist.
10. In Kubernetes, verify the WebService deployment references the Harbor image and the generated `pole-registry-*` image pull secret.
11. Verify the WebService release has `SourceBuild` action and provenance containing build job id, source repository id, exact commit SHA, image digest, previous image reference, and new image reference.
12. Trigger a second build after advancing the sample repository. Verify the first job keeps its original recipe/commit snapshot and the second job uses the newly reanalyzed commit.

## Failure checks

Run at least one failure case per implementation change:

- Stop BuildKit before queueing a build and verify the job becomes `failed` without exposing process output or credentials.
- Remove Harbor availability and verify the job fails with a provider-safe error.
- Attempt to delete a linked source repository or registry connection and verify Pole rejects it.
- Attempt to remove a build configuration while a build is active and verify Pole rejects it.
- Verify another tenant cannot read the connection, source repository, configuration, or build history.

## Cleanup

1. Remove the source build configuration and unlink the source repository.
2. Delete the WebService and resource group.
3. Remove test Harbor projects left by an interrupted run.
4. Delete test MongoDB/outbox records and Kubernetes namespaces only after confirming their tenant and test identifiers.
5. Stop `pole-harbor` and `pole-buildkit` from Aspire when they are no longer needed.
6. Confirm no test credentials, generated Harbor configuration, or local AppHost settings are tracked by Git.
