# Envelope Encryption Operations

Pole uses envelope encryption for sensitive values that it owns. A fresh data-encryption key
(DEK) protects each sensitive value version, and a configured key-encryption key (KEK) wraps that
DEK. The persisted envelope contains the algorithm version, provider ID, KEK ID and version,
wrapped DEK, ciphertext, nonce, and authentication tag. Tenant, owner, and value-version identity
is authenticated as associated data, so copying an envelope to another identity or changing its
identity metadata fails decryption.

The default provider is `local-key-file`. It is fully self-hosted: Pole reads each KEK from an
operator-created, read-only file and does not require an external service. Pole never generates,
replaces, or writes a KEK at runtime. Key material must not be stored in MongoDB, checked-in
configuration, normal appsettings, environment-variable values, or command output.

## Threat boundary

Envelope encryption protects sensitive values in MongoDB and MongoDB backups, and authenticated
encryption detects ciphertext, wrapped-key, and identity substitution. It does not protect values
from:

- a fully compromised running Pole process, which can access plaintext while performing authorized
  work;
- an operator or attacker who controls both a Pole process and the mounted KEK;
- plaintext captured before encryption or after decryption by an owning integration; or
- compromise of an external system to which Pole legitimately sends a credential.

Protect the key files, the runtime identity that can read them, deployment configuration, and key
backups as a separate security boundary. Do not store KEK backups beside MongoDB backups or grant
the same routine access to both.

## Configuration contract

Configuration lives under `EnvelopeEncryption`. The initial provider has the fixed ID
`local-key-file`:

```json
{
  "EnvelopeEncryption": {
    "ActiveProviderId": "local-key-file",
    "LocalKeyFile": {
      "ActiveKeyId": "pole-local",
      "ActiveKeyVersion": 1,
      "Keys": [
        {
          "Id": "pole-local",
          "Version": 1,
          "Path": "/run/pole-keys/envelope-kek-v1.b64"
        }
      ]
    }
  }
}
```

Each `Path` must be absolute and identify a separate read-only file. The file content is Base64 for
exactly 32 cryptographically random bytes. A trailing line ending is harmless, but do not add
comments, JSON, a key ID, or other text to the file. The key ring may contain one active key and
retained previous keys. Every `(Id, Version)` pair must be unique, and the configured active pair
must appear in `Keys`.

Configuration validation fails closed when the provider, active pair, key entry, path, Base64, or
decoded key length is missing or invalid. Diagnostics identify configuration metadata, never key
bytes, plaintext, ciphertext, or wrapped DEKs. Never reuse an `(Id, Version)` pair for different
key bytes. If bytes change, allocate a new version (or a new ID and version).

Application hosts that deliberately register a different `IKeyEncryptionProvider` can call
`AddEnvelopeEncryption(configuration, includeLocalKeyFileProvider: false)` so an unused LocalKeyFile
ring is not required. The host must register exactly one provider for the configured active ID and
must retain every provider needed by stored envelopes. External KMS provider implementations remain
outside this change.

## Bootstrap a key

Generate the first KEK once, outside Pole, on a trusted host. Restrict the file before starting
Pole. The examples intentionally produce no key material on standard output.

### Linux

The following example assumes a service account named `pole`; adjust the account and location for
the installation:

```bash
sudo install -d -m 0700 -o pole -g pole /etc/pole/keys
sudo -u pole sh -c 'umask 077; openssl rand 32 | openssl base64 -A > /etc/pole/keys/envelope-kek-v1.b64'
sudo chmod 0400 /etc/pole/keys/envelope-kek-v1.b64
```

Configure the absolute path `/etc/pole/keys/envelope-kek-v1.b64` for a direct process, or mount that
file at a different absolute path inside a container. Do not run `cat`, `Get-Content`, `echo`, or a
debug command that prints the file.

### PowerShell

Run this in an elevated shell. Replace the service account with the identity that runs Pole:

```powershell
$keyDirectory = 'C:\ProgramData\Pole\keys'
$keyPath = Join-Path $keyDirectory 'envelope-kek-v1.b64'
$serviceAccount = "$env:COMPUTERNAME\PoleService"

New-Item -ItemType Directory -Force -Path $keyDirectory | Out-Null
$keyBytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
[System.IO.File]::WriteAllText(
    $keyPath,
    [System.Convert]::ToBase64String($keyBytes),
    [System.Text.Encoding]::ASCII)
[System.Array]::Clear($keyBytes, 0, $keyBytes.Length)

icacls $keyDirectory /inheritance:r /grant:r "${serviceAccount}:(OI)(CI)RX" "Administrators:(OI)(CI)F" | Out-Null
icacls $keyPath /inheritance:r /grant:r "${serviceAccount}:R" "Administrators:F" | Out-Null
```

The key file is not a substitute for a backup. Create and test a separately protected backup before
using the key to encrypt values.

## Aspire local development

Keep the KEK in a host file and put only its path and non-secret identity metadata in the required
AppHost parameters. For example, after creating
`C:\ProgramData\Pole\keys\envelope-kek-v1.b64`:

```powershell
dotnet user-secrets set "Parameters:envelope-encryption-active-key-id" "pole-local" --project src/api/Pole.AppHost/Pole.AppHost.csproj
dotnet user-secrets set "Parameters:envelope-encryption-active-key-version" "1" --project src/api/Pole.AppHost/Pole.AppHost.csproj
dotnet user-secrets set "Parameters:envelope-encryption-active-key-path" "C:\ProgramData\Pole\keys\envelope-kek-v1.b64" --project src/api/Pole.AppHost/Pole.AppHost.csproj
```

AppHost forwards this configuration to `pole-server`; it does not copy, generate, or back up the
key. The path must therefore be absolute and readable by the server process. User secrets are a
development configuration mechanism, not production key storage.

The default AppHost parameters describe one active development key. To rehearse retained-key
rotation locally, set the optional retained-key parameter triplet. All three values are required
when any one is present, and AppHost forwards the retained key as `Keys:1`:

```powershell
dotnet user-secrets set "Parameters:envelope-encryption-retained-key-id" "pole-local" --project src/api/Pole.AppHost/Pole.AppHost.csproj
dotnet user-secrets set "Parameters:envelope-encryption-retained-key-version" "2" --project src/api/Pole.AppHost/Pole.AppHost.csproj
dotnet user-secrets set "Parameters:envelope-encryption-retained-key-path" "C:\ProgramData\Pole\keys\envelope-kek-v2.b64" --project src/api/Pole.AppHost/Pole.AppHost.csproj
```

During phase 1, keep the old key in the active parameters and put the new key in the retained
parameters. During phase 2, swap them so the new key is active and the old key remains retained.
Use direct `pole-server` configuration when more than one retained key is required. Never put key
bytes in these settings.

## Direct and container deployment

For a direct service, configure `EnvelopeEncryption` through the normal configuration providers and
point `Path` at the secured host file. Configuration may contain the ID, version, and path, but never
the file content.

For a container, mount every key file individually as read-only and use its absolute in-container
path. This abbreviated `docker run` example shows the configuration mapping:

```bash
docker run \
  --mount type=bind,src=/srv/pole/keys/envelope-kek-v1.b64,dst=/run/pole-keys/envelope-kek-v1.b64,readonly \
  -e EnvelopeEncryption__ActiveProviderId=local-key-file \
  -e EnvelopeEncryption__LocalKeyFile__ActiveKeyId=pole-local \
  -e EnvelopeEncryption__LocalKeyFile__ActiveKeyVersion=1 \
  -e EnvelopeEncryption__LocalKeyFile__Keys__0__Id=pole-local \
  -e EnvelopeEncryption__LocalKeyFile__Keys__0__Version=1 \
  -e EnvelopeEncryption__LocalKeyFile__Keys__0__Path=/run/pole-keys/envelope-kek-v1.b64 \
  pole-server-image
```

Grant read access only to the container identity. Do not bake key files into an image or image
layer, mount them read-write, or put their Base64 content in an environment variable.

## Kubernetes deployment

Create a Kubernetes Secret from the already generated file without placing its content in a
manifest or shell argument:

```bash
kubectl create secret generic pole-envelope-keys \
  --namespace pole \
  --from-file=envelope-kek-v1.b64=/secure/pole/envelope-kek-v1.b64
```

The command prints only resource status, not the key. Kubernetes Secret values are not necessarily
encrypted in etcd; enable Kubernetes encryption at rest, restrict RBAC and node access, and include
the Secret in the separate key-backup security boundary.

Mount the Secret read-only and set only key metadata and paths as environment variables:

```yaml
spec:
  replicas: 3
  template:
    spec:
      securityContext:
        # Match the non-root group used by the Pole Server image.
        fsGroup: 1654
      containers:
        - name: pole-server
          env:
            - name: EnvelopeEncryption__ActiveProviderId
              value: local-key-file
            - name: EnvelopeEncryption__LocalKeyFile__ActiveKeyId
              value: pole-local
            - name: EnvelopeEncryption__LocalKeyFile__ActiveKeyVersion
              value: "1"
            - name: EnvelopeEncryption__LocalKeyFile__Keys__0__Id
              value: pole-local
            - name: EnvelopeEncryption__LocalKeyFile__Keys__0__Version
              value: "1"
            - name: EnvelopeEncryption__LocalKeyFile__Keys__0__Path
              value: /run/pole-keys/envelope-kek-v1.b64
          volumeMounts:
            - name: envelope-keys
              mountPath: /run/pole-keys
              readOnly: true
      volumes:
        - name: envelope-keys
          secret:
            secretName: pole-envelope-keys
            defaultMode: 0440
            items:
              - key: envelope-kek-v1.b64
                path: envelope-kek-v1.b64
```

Prefer versioned, immutable Secret names during rotation so a rollout has an auditable, consistent
key ring. Do not rely on an in-place Secret update reaching every pod at the same instant.
The shown `fsGroup` matches the current .NET image default; use the actual Pole process group when
the image security context is customized.

## Multi-replica requirements

Every replica must map each configured `(Id, Version)` to exactly the same 32 bytes. Paths may differ
between hosts, but identity-to-bytes mappings may not. All replicas must receive the complete key
ring needed by current records before any replica writes with a new active key.

Keep the same active provider and active key pair across the steady-state deployment. During the
controlled rotation window, old-active and new-active replicas may briefly coexist only after both
keys have been distributed to every replica. A replica with a missing key must fail closed instead
of generating a replacement or returning partial plaintext.

## Backup and restore

Back up key files independently from MongoDB:

1. Record the provider ID and every `(Id, Version)` mapping in a protected key inventory. Paths are
   deployment details and may change on restore.
2. Copy each key file to an access-controlled, encrypted backup system that is operationally
   separate from MongoDB backups. Preserve the file bytes exactly.
3. Restrict and audit restore access. Anyone with a matching database backup and KEK can decrypt its
   protected values.
4. Retain each old KEK while any supported database backup or live envelope may reference it.
5. Test restoration in an isolated environment: restore MongoDB, restore the required key files,
   configure their original IDs and versions, start Pole, and perform owner-specific decrypt checks.
   Report only pass/fail and non-sensitive identifiers.

Never validate a backup by printing key or plaintext content. A backup is not considered usable
until the database and exact key ring have been restored together successfully.

When setting retention, first complete and verify owner-specific DEK rewraps. Then retain the old
KEK until the last recovery point that can contain an envelope wrapped by it has expired and a newer
restore point has passed a restore test. Removing an old key from the live key ring does not remove
the need for its protected backup while supported recovery points still reference it.

## KEK rotation

Rotation is a two-phase rolling change. Never replace bytes in an existing key file or reuse its
`(Id, Version)`.

### Phase 1: distribute the new key

1. Generate and back up a new 32-byte KEK using the bootstrap procedure, with a new version.
2. Add both old and new entries to `LocalKeyFile.Keys`, but leave `ActiveKeyId` and
   `ActiveKeyVersion` pointing to the old key.
3. Mount both files read-only on every replica and roll out the deployment.
4. Verify every replica starts and can read envelopes wrapped by the old key. Exercise a controlled
   test envelope with the new retained key if the owning test workflow supports it.

Do not continue until every replica has an identical ID/version-to-bytes mapping for both keys.

### Phase 2: activate and rewrap

1. Change `ActiveKeyId` and `ActiveKeyVersion` to the new pair, retaining both entries in `Keys`.
2. Roll out all replicas. Mixed old-active/new-active replicas are safe during this bounded window
   because both key versions are already present everywhere.
3. Verify new sensitive-value versions use the new KEK metadata and that old envelopes remain
   readable.
4. Each storage owner must enumerate its encrypted records, request a DEK rewrap to the active key,
   and durably persist the returned envelope using its own concurrency and transaction rules. DEK
   rewrap unwraps and rewraps only the DEK; it does not decrypt or rewrite the value ciphertext.
   For the container-registry credential owner, a platform administrator runs the built-in batch
   endpoint after every replica has the new key active:

   ```bash
   curl --fail-with-body \
     --request POST https://pole.example/api/v1/platform/encryption/container-registry-credentials/rewrap \
     --header "Authorization: Bearer $POLE_PLATFORM_ADMIN_TOKEN" \
     --header "Content-Type: application/json" \
     --data '{"providerId":"local-key-file","keyId":"pole-local","keyVersion":2,"keyWrapAlgorithm":"AES-256-GCM","keyWrapAlgorithmVersion":1}'
   ```

   The endpoint refuses to run on a replica whose active key does not exactly match the requested
   target. It processes bounded MongoDB batches and uses an optimistic envelope comparison before
   each update. Repeat it when `concurrentChanges` is nonzero.
5. Verify, per owner, that no live envelope references the old pair. Recheck after concurrent writes
   and after all old-active replicas have terminated.
6. Keep the old KEK in the runtime key ring until those checks pass. Keep its backup for the backup
   retention period described above; only then remove it from the appropriate live and backup key
   rings.

There is no global rewrap command in this change. Every adopting owner must provide and validate
its own enumeration, optimistic concurrency, retry, and persistence workflow. For the current
container-registry owner, rerun the endpoint until a verification run reports zero `rewrapped` and
zero `concurrentChanges`. Removing an old key before every owner has completed that work makes
remaining values unreadable.

## Key compromise

Treat a suspected KEK disclosure as an incident:

1. Preserve evidence without logging or copying key/plaintext material into the incident system.
2. Generate a new KEK, distribute it, activate it, and perform every owner-specific DEK rewrap using
   the two-phase procedure.
3. Determine whether the attacker could also access MongoDB or its backups. Rewrapping does not make
   already copied ciphertext safe when the attacker also obtained its old KEK.
4. Rotate or revoke the underlying credentials and sensitive values whose confidentiality may have
   been lost; this creates new value versions and DEKs. Rebuild clean backups and handle affected
   backup copies according to the incident and retention policy.
5. Remove the compromised KEK from runtime only after live references have been eliminated. Retain
   or destroy backup copies according to the incident decision, understanding that destroying them
   also makes matching recovery points unrecoverable.

## Unrecoverable key loss

If the exact bytes for a referenced `(provider ID, key ID, key version)` are lost, Pole cannot
decrypt the affected values. There is no fallback, master key, or safe way to recreate the bytes.
Restore the exact key from its protected backup. If no copy exists, the owner must reprovision or
replace the affected credentials/values through an owner-specific recovery workflow, and any backup
that depends on that key is permanently unusable. Never generate new bytes under the lost ID and
version: that hides the operational cause but cannot decrypt existing data.

## Legacy registry credentials

Container registry credentials written with the previous ASP.NET Core Data Protection format are
not compatible with envelope-encrypted values. This issue deliberately provides no legacy-data
migration. Configure and back up envelope encryption before enabling an owner that adopts it, then
reprovision existing registry credentials through the container-registry owner's supported workflow.
Coordinate removal/recreation of the corresponding remote robot credentials so stale credentials
are revoked; deleting only MongoDB records is not a credential rotation. Until reprovisioning is
complete, legacy entries fail closed and builds or pulls that depend on them may be unavailable.

## Registry provisioning recovery

Harbor project ownership is deliberately outside automatic provisioning compensation. A tenant
project may predate the current request and may already contain repositories, so Pole never deletes
a Harbor project or repository after credential encryption or MongoDB persistence fails. It revokes
only the build and runtime robot credentials created by that provisioning attempt.

Each provisioning attempt is assigned a durable 32-character attempt ID before Harbor is called.
Pole derives the build and runtime robot names from that ID, so the same names can be reconstructed
after a restart even when Harbor created a robot but the create response was lost. When Harbor does
return robot IDs, Pole also records them in the non-secret `RemoteProvisioned` state before it
encrypts or persists credentials.

Before any robot deletion Pole atomically moves that exact attempt to `CleanupRequired`; this
compare-and-set is the safety barrier for an uncertain MongoDB commit. If the transaction actually
reached `Ready`, the cleanup claim cannot replace it and the robots remain active. A failed or
ambiguous Harbor create is also moved to `CleanupRequired`, with the deterministic names and any
known IDs. Name-only cleanup sweeps Harbor once per minute but remains durable for a ten-minute
settle horizon, which is longer than Pole's two-minute Harbor request timeout. A sweep that finds a
robot extends verification until a later empty sweep, so a delayed pull robot cannot appear after
the state has already closed. Stale `Provisioning` and `RemoteProvisioned` attempts are claimed
after ten minutes. Cleanup queries Harbor by deterministic name, requires both the attempt-specific
name suffix and a returned permission namespace matching the expected Harbor project, and deletes
every matching or already-known robot ID. Known IDs are deleted directly without a global name
search.

Before each Harbor cleanup sweep, a worker acquires a five-minute durable lease on the exact
`CleanupRequired` state through a MongoDB compare-and-set. The lease token is required by every
subsequent state update, and the external work is bounded to four minutes, so a stale worker cannot
close or overwrite a newer verification sweep. Concurrent replicas skip a cleanup while its lease
is active. If a worker crashes, the lease expires and another replica can recover the same cleanup
without operator intervention.

If cleanup fails, the project stays in `CleanupRequired` with sanitized failure metadata and cannot
be leased again before the one-minute retry delay. Name-only cleanup also extends its verification
deadline through that delay, so an already-queued stale worker cannot close the state immediately
after a partial failed sweep. The `cleanup-container-registry-provisioning` Hangfire job retries
eligible records every minute. Robot deletion treats Harbor `404` as success, so retries and
multiple Pole replicas are idempotent.
Operators should alert on persisting `CleanupRequiredRegistryProjectProvisioning` records and
Harbor-cleanup warning logs; manual recovery must delete only robots whose names match the recorded
attempt ID (or their recorded IDs), never the tenant project.

## Plaintext handling rules

Plaintext exists only in the owning operation's process memory for the shortest practical time. It
must never be added to:

- structured or console logs, exception messages, traces, activity tags, or baggage;
- audit payloads, metrics and labels, or diagnostic dumps;
- domain/integration events or MassTransit messages;
- outbox records or retry/error messages; or
- search documents or indexes.

Events and outbox messages should carry only the minimum non-sensitive owner identity and version
needed by a consumer. Search projections must index only explicitly non-sensitive metadata. Error
paths return stable, non-sensitive diagnostics and must not interpolate plaintext, key content,
ciphertext, wrapped DEKs, or serialized envelopes. Tests and restore drills must use synthetic
values and assert that neither the value nor its encoded forms appear in captured diagnostics.
