# SeaweedFS Community operator contract

Pole pins the official [operator Helm release 0.1.47](https://github.com/seaweedfs/seaweedfs-operator/releases/tag/seaweedfs-operator-0.1.47),
whose chart runs `chrislusf/seaweedfs-operator:1.0.44`. Its archive SHA-256 is
`fe16b903e5a06f8d9fba6a7822362fc0bb1262e641c284453a5747126fd22815`, matching the
[official Helm index](https://seaweedfs.github.io/seaweedfs-operator/index.yaml).
The pinned CNPG chart `0.29.0` currently runs controller `1.30.0`, including native Database,
DatabaseRole and failover-quorum CRDs. Workloads use Community image `docker.io/chrislusf/seaweedfs:4.53`, the
[stable 4.53 release](https://github.com/seaweedfs/seaweedfs/releases/tag/4.53).
The server and operator versions are independent; neither uses `latest`.

The namespaced provider API is `seaweed.seaweedfs.com/v1`. Pole needs `Seaweed`, `Bucket`,
`S3Identity`, `S3Credentials`, `S3Policy`, `S3PolicyBinding`, and, for replicated mode,
`AdminScript`. The chart also ships optional backup, restore, reference-grant, OIDC, role,
lifecycle, notification, and CSI CRDs. Pole does not enable the node-global CSI controller.

## Supported workload topology

| Mode                                     | Master | Volume | Filer | S3 gateway | PostgreSQL metadata | Steady pods | PVCs |
| ---------------------------------------- | -----: | -----: | ----: | ---------: | ------------------: | ----------: | ---: |
| SingleNode                               |      1 |      1 |     1 |          1 |                   0 |           4 |    3 |
| Replicated, dedicated metadata           |      3 |      3 |     3 |          3 |                   3 |          15 |    9 |
| Replicated, existing eligible PostgreSQL |      3 |      3 |     3 |          3 |        0 additional |          12 |    6 |

Every master has a 1 GiB PVC for Raft identity/log persistence. Each volume server has one
PVC of the requested volume size. SingleNode uses a persistent 1 GiB local LevelDB2 filer
store. Replicated uses three persistent 1 GiB CNPG metadata instances and stateless filers.
Thus provisioned storage is `requested GiB + 2 GiB` for SingleNode and
`3 * requested GiB + 6 GiB` for Replicated with dedicated metadata. An existing eligible tenant
PostgreSQL resource saves three additional metadata pods and their three GiB of provisioned storage;
its provider owns a dedicated per-instance database/login and the qualified connection address. Shared operator pods and transient CNPG bootstrap
or repair Jobs are additional infrastructure overhead. Each steady workload pod adopts the
resolved compute requests/limits and placement snapshot.

Replication `001` means two physical copies on different volume servers in the same rack.
Hard component anti-affinity on `kubernetes.io/hostname` puts the three replicas of every
component on independent worker nodes. This proves node separation, not independent physical
hosts/disks when the test workers are containers on one host. Physical volume files are bounded to 64 MiB. Their per-node maximum count is
`(requested GiB * 1024 - 64) / 64`, reserving 64 MiB for filesystem/index overhead, with a 1%
minimum free-space stop. Master configuration sets every `master.volume_growth.copy_*` count to one logical volume,
including the internal metadata-log collection. Bucket placement requests growth `1` for replication `000` or `2` for `001`, the smallest valid
positive values: stable `fs.configure` requires the count to be divisible by the number of copies. This avoids wasting slots on upstream default pre-growth. These bounds support requested capacity through 16 TiB.

Repair additionally requires enough free volume slots and physical capacity on surviving nodes
to replace the lost copies. A completely full three-node cluster cannot repair into two nodes
merely because replication is enabled. Plan free capacity and monitor it; Pole does not impose
an aggregate S3 byte-consumption quota. HA acceptance keeps real spare space and verifies
two-copy restoration while the failed worker is still offline.

Managed buckets also request
`placement.fsync: true`, so object acknowledgements wait for durable volume writes.

Replicated metadata requires the pinned CNPG controller on the deployment target. The database
uses PostgreSQL 17, three instances, hard hostname anti-affinity and:

```yaml
postgresql:
    synchronous:
        method: any
        number: 1
        dataDurability: required
        failoverQuorum: true
```

The operator must support all four fields. Required durability pauses writes when a synchronous
standby is unavailable; failover quorum prevents promoting an unproven stale replica. A single
worker failure leaves two database instances and two volume servers available. Availability
means recovery after bounded leader election and routing convergence, not zero interrupted
requests. See [CNPG replication](https://cloudnative-pg.io/docs/1.29/replication/) and
[failover quorum](https://cloudnative-pg.io/docs/current/failover/).

The Seaweed operator does not turn local LevelDB replicas into a synchronous metadata quorum.
The stable server's [metadata notification path](https://github.com/seaweedfs/seaweedfs/blob/4.53/weed/filer/filer_notify.go)
buffers events for asynchronous peer consumption. Replicated mode instead uses its built-in
[PostgreSQL adapter](https://github.com/seaweedfs/seaweedfs/blob/4.53/weed/filer/postgres/postgres_store.go)
against the instance-owned CNPG service. `connection_max_open=0` preserves the adapter's
unbounded concurrent connection default because bounded pools can deadlock nested listing.

## Manifests and credentials

Native names derive from immutable Pole instance IDs. `spec.labels` and per-component
`spec.<component>.labels` carry the complete persisted workload identity. The standalone gateway
Service is `<instance>-s3:8333`. Its pods carry `app.kubernetes.io/component=s3` plus the full
Pole identity, so private-access policies can select the gateway without exposing master,
volume, filer, metadata, or operator endpoints.

The exact secret selectors are `spec.s3.configSecret: {name, key: s3.json}` and
`spec.filer.configSecret: {name, key: filer.toml}`. Secret contents never belong in custom
resources. The bootstrap S3 JSON contains one randomly generated out-of-band administrator:

```json
{
    "identities": [
        {
            "name": "pole-bootstrap-admin",
            "credentials": [
                { "accessKey": "<generated>", "secretKey": "<generated>" }
            ],
            "actions": ["Admin"]
        }
    ]
}
```

Authentication is configured before the gateway starts: SeaweedFS otherwise permits anonymous
access when no credentials exist. PostgreSQL uses a different generated application password in
a create-only owned `kubernetes.io/basic-auth` Secret. Its initdb database/owner are `seaweedfs`;
the same password is rendered into the filer's private configuration Secret, with TLS required
for the database connection. Bootstrap credentials and metadata passwords must remain stable
across reconciliation and be retrieved only through their explicit write-only managed paths.

`S3Credentials.spec.secretRef` can adopt a complete externally managed Secret read-only. The
operator watches same-namespace Secret updates, provisions the new key, and revokes the old
key. Its `Ready` condition can still describe the previous Secret content during rotation;
readiness must compare `status.accessKey` with the current projected access key. A Secret
owned by another `S3Credentials`, or an incomplete externally owned Secret, is rejected.

Replicated instances schedule `AdminScript` every minute with `lock`,
`volume.fix.replication -apply`, and `unlock`. The `-apply` flag is required: stable
[4.53 defaults to simulation](https://github.com/seaweedfs/seaweedfs/blob/4.53/weed/shell/command_volume_fix_replication.go).
The Job has a 300-second active deadline and `Forbid` concurrency. A Job scheduled just before
a failed node becomes NotReady can hold the next run until this deadline; repair acceptance
allows that delay rather than mistaking initial successful S3 recovery for repaired redundancy.
On Kubernetes 1.31+, a terminal Job condition can also wait for a pod on an unreachable node to
terminate. If an actually lost worker remains fenced off, infrastructure operators may remove
its Node object to let Kubernetes finish pod cleanup. Pole does not delete Nodes or infer that
NotReady means powered off. The isolated runner records unassisted progress separately; only
after confirming its own runtime is stopped does it optionally remove that owned Node and prove
repair across the two surviving workers, before restarting the old runtime. It never adds a
replacement worker. The native Seaweed shell lock expires after ten seconds but is advisory;
its acquisition is not an atomic check-and-create and volume mutations do not verify fencing
tokens, so it does not justify concurrent automated repair jobs. `Forbid` remains deliberate.
`spec.clusterPodLabels` carries the same full Pole labels to the Job pod so internal network
policy admits its repair traffic; the operator also temporarily applies/restores those labels
on cluster pods for the Job's duration. PVC ownership labels use `master.persistence.labels`,
`filer.persistence.labels`, `volume.storageLabels`, and CNPG `inheritedMetadata.labels`.

## Bucket retirement and administrative trust

Bucket retirement retains data and attaches an explicit bucket policy denying `s3:*` to `*`
on both `arn:aws:s3:::<bucket>` and `arn:aws:s3:::<bucket>/*`. It must deny ordinary credentials
created outside Pole's managed key path as well as the original key. Revoking one credential
alone cannot enforce bucket retirement. Sibling buckets remain usable.

The pinned server evaluates ordinary S3 bucket-policy denies even for native Admin credentials;
other administrative handlers, direct filer/IAM commands, and operator reconciliation remain
trusted paths for changing that policy. Kubernetes administrators,
direct filer/volume/master access, storage administrators, and the trusted operator also remain
outside the tenant S3 boundary. Pole must keep those administrative paths private. This is
bucket retirement, not protection against trusted administrators or a WORM retention feature.
Instance retirement must revoke private S3 ingress as well as its managed credentials; deleting
one Service or projected key alone does not close an existing private access path.

Native instance retirement removes `spec.s3` from the Seaweed custom resource. Operator 1.0.44
then prunes the standalone gateway Deployment, Service, per-component Ingress and ServiceMonitor
([exact reconciler source](https://github.com/seaweedfs/seaweedfs-operator/blob/1.0.44/internal/controller/controller_s3.go#L91)).
It retains master, volume, filer, metadata and credentials. Restoring `spec.s3` can serve the same
key/data again; Pole must still explicitly revoke its private-access ingress state during retirement.
The isolated acceptance runner checks deletion of gateway Deployment/Service/pods and restoration
of the same rotated key/data, with the previous key still revoked.
