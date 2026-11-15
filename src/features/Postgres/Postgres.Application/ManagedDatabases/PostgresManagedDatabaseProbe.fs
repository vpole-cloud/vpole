namespace Pole.Features.Postgres.Application.ManagedDatabases

open System
open System.Collections.Generic
open System.Net
open System.Threading
open k8s
open k8s.Autorest
open k8s.Models
open MongoDB.Bson
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Secrets.Contracts

[<RequireQualifiedAccess>]
module PostgresManagedDatabaseProbeManifests =
    [<Literal>]
    let ProbeLabel = "pole.io/postgres-database-probe"
    [<Literal>]
    let TokenAnnotation = "pole.io/postgres-database-probe-token"
    let name (claim: PostgresManagedDatabase) = $"pole-db-probe-{claim.Id}"
    let policyName (claim: PostgresManagedDatabase) = $"pole-db-probe-access-{claim.Id}"
    let token (placement: ResourceDeploymentRuntimePlacementDto) clusterUid nativeGeneration runtimeEpoch secretResourceVersion (projection: SecretProjectionReference) =
        $"2:{clusterUid}:{nativeGeneration}:{runtimeEpoch}:{placement.DesiredGeneration}:{projection.AppliedVersion}:{secretResourceVersion}"

    let policy (claim: PostgresManagedDatabase) =
        V1NetworkPolicy(ApiVersion = "networking.k8s.io/v1", Kind = "NetworkPolicy",
            Metadata = V1ObjectMeta(Name = policyName claim, NamespaceProperty = claim.Target.KubernetesNamespace,
                Labels = PostgresManagedDatabaseManifests.labels claim, Annotations = PostgresManagedDatabaseManifests.annotations claim),
            Spec = V1NetworkPolicySpec(PolicyTypes = ResizeArray [ "Ingress" ],
                PodSelector = V1LabelSelector(MatchLabels = dict [
                    "pole.io/tenant-id", claim.TenantId; "pole.io/runtime-cell-id", claim.Target.RuntimeCellId
                    "pole.io/resource-deployment-id", claim.Target.DeploymentId ]),
                Ingress = ResizeArray [ V1NetworkPolicyIngressRule(
                    Ports = ResizeArray [ V1NetworkPolicyPort(Protocol = "TCP", Port = IntOrString.op_Implicit(5432)) ],
                    FromProperty = ResizeArray [ V1NetworkPolicyPeer(
                        NamespaceSelector = V1LabelSelector(MatchLabels = dict [
                            "kubernetes.io/metadata.name", claim.Target.KubernetesNamespace
                            "pole.io/tenant-id", claim.TenantId; "pole.io/runtime-cell-id", claim.Target.RuntimeCellId ]),
                        PodSelector = V1LabelSelector(MatchLabels = dict [
                            "pole.io/tenant-id", claim.TenantId; "pole.io/runtime-cell-id", claim.Target.RuntimeCellId
                            "pole.io/resource-deployment-id", claim.Target.DeploymentId; ProbeLabel, claim.Id ])) ] ) ]))

    let job (claim: PostgresManagedDatabase) (cluster: PostgresCluster) (placement: ResourceDeploymentRuntimePlacementDto)
            clusterUid nativeGeneration runtimeEpoch secretResourceVersion (projection: SecretProjectionReference) (settings: PostgresSettings) =
        if not (ObjectId.TryParse(claim.Id) |> fst) ||
           claim.Database <> $"pole_db_{claim.Id}" || claim.Username <> $"pole_role_{claim.Id}" then
            invalidOp "Managed database names do not match their retained claim authority."
        let labels = Dictionary<string, string>(PostgresManagedDatabaseManifests.labels claim)
        labels["pole.io/resource-id"] <- claim.ClusterId
        labels[ProbeLabel] <- claim.Id
        let annotations = Dictionary<string, string>(PostgresManagedDatabaseManifests.annotations claim)
        annotations[TokenAnnotation] <- token placement clusterUid nativeGeneration runtimeEpoch secretResourceVersion projection
        let host = $"pg-{claim.ClusterId}-rw.{claim.Target.KubernetesNamespace}.svc.cluster.local"
        // Generated identifiers contain only a fixed prefix and ObjectId. No user SQL or credential value is interpolated.
        let sql =
            $"""BEGIN;
SET LOCAL statement_timeout = '15000ms';
DO $probe$ BEGIN
 IF current_database() <> '{claim.Database}' OR current_user <> '{claim.Username}'
    OR current_setting('synchronous_commit') NOT IN ('on', 'remote_apply')
    OR current_setting('synchronous_standby_names') !~ '^ANY[[:space:]]+1[[:space:]]*[(]'
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = current_user
        AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR rolinherit))
    OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_database d JOIN pg_catalog.pg_roles r ON r.oid = d.datdba
        WHERE d.datname = current_database() AND r.rolname = current_user) THEN
   RAISE EXCEPTION 'managed database verification failed';
 END IF;
END $probe$;
REVOKE CONNECT, CREATE, TEMPORARY ON DATABASE "{claim.Database}" FROM PUBLIC;
COMMIT;
BEGIN;
SET LOCAL statement_timeout = '15000ms';
DO $acl$ BEGIN
 IF EXISTS (SELECT 1 FROM pg_catalog.pg_database d,
     LATERAL pg_catalog.aclexplode(COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))) acl
     WHERE d.datname = current_database() AND acl.grantee = 0
       AND acl.privilege_type IN ('CONNECT', 'CREATE', 'TEMPORARY')) THEN
   RAISE EXCEPTION 'managed database public privileges were not revoked';
 END IF;
END $acl$;
CREATE TEMP TABLE pole_metadata_verification (value integer) ON COMMIT DROP;
INSERT INTO pole_metadata_verification VALUES (1);
ROLLBACK;"""
        let compute = PostgresResourceDeploymentMappings.kubernetesCompute placement.Compute
        let environment = ResizeArray [
            V1EnvVar(Name = "PGHOST", Value = host); V1EnvVar(Name = "PGPORT", Value = "5432")
            V1EnvVar(Name = "PGDATABASE", Value = claim.Database); V1EnvVar(Name = "PGSSLMODE", Value = "require")
            V1EnvVar(Name = "PGCONNECT_TIMEOUT", Value = "5")
            V1EnvVar(Name = "PGUSER", ValueFrom = V1EnvVarSource(SecretKeyRef = V1SecretKeySelector(Name = projection.KubernetesName, Key = "username")))
            V1EnvVar(Name = "PGPASSWORD", ValueFrom = V1EnvVarSource(SecretKeyRef = V1SecretKeySelector(Name = projection.KubernetesName, Key = "password"))) ]
        let pod = V1PodSpec(RestartPolicy = "Never", AutomountServiceAccountToken = Nullable false,
                    NodeSelector = compute.ToNodeSelector(), Tolerations = compute.ToTolerations(),
                    Affinity = V1Affinity(NodeAffinity = compute.ToNodeAffinity()),
                    SecurityContext = V1PodSecurityContext(RunAsNonRoot = Nullable true, SeccompProfile = V1SeccompProfile(Type = "RuntimeDefault")),
                    Containers = ResizeArray [ V1Container(Name = "verify", Image = PostgresSettings.buildImageReference settings.OperatorImage cluster.Spec.Version,
                        Command = ResizeArray [ "psql" ], Args = ResizeArray [ "-X"; "--no-password"; "--set=ON_ERROR_STOP=1"; "--command"; sql ], Env = environment,
                        Resources = V1ResourceRequirements(Requests = dict [ "cpu", ResourceQuantity("100m"); "memory", ResourceQuantity("64Mi") ],
                            Limits = dict [ "cpu", ResourceQuantity("250m"); "memory", ResourceQuantity("128Mi") ]),
                        SecurityContext = V1SecurityContext(AllowPrivilegeEscalation = Nullable false,
                            ReadOnlyRootFilesystem = Nullable true, Capabilities = V1Capabilities(Drop = ResizeArray [ "ALL" ]))) ])
        V1Job(ApiVersion = "batch/v1", Kind = "Job",
            Metadata = V1ObjectMeta(Name = name claim, NamespaceProperty = claim.Target.KubernetesNamespace,
                Labels = labels, Annotations = annotations, OwnerReferences = ResizeArray [ V1OwnerReference(
                    ApiVersion = "postgresql.cnpg.io/v1", Kind = "Cluster", Name = PostgresManagedDatabaseManifests.clusterName claim,
                    Uid = clusterUid, BlockOwnerDeletion = Nullable true) ]),
            Spec = V1JobSpec(BackoffLimit = Nullable 0, ActiveDeadlineSeconds = Nullable 60L,
                Template = V1PodTemplateSpec(Metadata = V1ObjectMeta(Labels = labels), Spec = pod)))


type PostgresManagedDatabaseProbe(native: PostgresManagedDatabaseNative, settings: PostgresSettings) =
    member _.DeleteAsync(client: IKubernetes, claim: PostgresManagedDatabase, ct: CancellationToken) =
        task {
            try
                let! current = client.BatchV1.ReadNamespacedJobAsync(PostgresManagedDatabaseProbeManifests.name claim, claim.Target.KubernetesNamespace, cancellationToken = ct)
                if not (PostgresManagedDatabaseManifests.owns claim current.Metadata) ||
                   String.IsNullOrWhiteSpace current.Metadata.Uid || String.IsNullOrWhiteSpace current.Metadata.ResourceVersion then return false
                else
                    let! _ = client.BatchV1.DeleteNamespacedJobAsync(current.Metadata.Name, current.Metadata.NamespaceProperty,
                                    body = V1DeleteOptions(PropagationPolicy = "Foreground",
                                        Preconditions = V1Preconditions(Uid = current.Metadata.Uid, ResourceVersion = current.Metadata.ResourceVersion)), cancellationToken = ct)
                    return false
            with
            | :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.NotFound -> return true
            | :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.Conflict -> return false
        }

    member this.VerifyAsync(client: IKubernetes, claim: PostgresManagedDatabase, cluster: PostgresCluster,
                            placement: ResourceDeploymentRuntimePlacementDto, clusterUid: string, nativeGeneration: int64, runtimeEpoch: string, secretResourceVersion: string, projection: SecretProjectionReference,
                            isCurrent: CancellationToken -> System.Threading.Tasks.Task<bool>, ct: CancellationToken) =
        task {
            let! current =
                task {
                    try
                        let! job = client.BatchV1.ReadNamespacedJobAsync(PostgresManagedDatabaseProbeManifests.name claim, claim.Target.KubernetesNamespace, cancellationToken = ct)
                        return Some job
                    with :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.NotFound -> return None
                }
            let desired = PostgresManagedDatabaseProbeManifests.job claim cluster placement clusterUid nativeGeneration runtimeEpoch secretResourceVersion projection settings
            match current with
            | Some job when not (PostgresManagedDatabaseManifests.owns claim job.Metadata) -> return false
            | Some job when job.Metadata.DeletionTimestamp.HasValue -> return false
            | Some job when isNull job.Metadata.Annotations ||
                            not (job.Metadata.Annotations.ContainsKey PostgresManagedDatabaseProbeManifests.TokenAnnotation) ||
                            job.Metadata.Annotations[PostgresManagedDatabaseProbeManifests.TokenAnnotation] <> desired.Metadata.Annotations[PostgresManagedDatabaseProbeManifests.TokenAnnotation] ||
                            (not (isNull job.Status) && job.Status.Failed.GetValueOrDefault() > 0) ->
                let! _ = this.DeleteAsync(client, claim, ct)
                return false
            | Some job when not (isNull job.Status) && job.Status.Succeeded.GetValueOrDefault() = 1 ->
                return! native.DeletePolicyAsync(client, claim, PostgresManagedDatabaseProbeManifests.policyName claim, ct)
            | _ ->
                let! allowed = native.ApplyPolicyAsync(client, claim, PostgresManagedDatabaseProbeManifests.policy claim, isCurrent, ct)
                if not allowed then return false
                else
                    match current with
                    | Some _ -> return false
                    | None ->
                        let! authorized = isCurrent ct
                        if not authorized then return false
                        else
                            try
                                let! _ = client.BatchV1.CreateNamespacedJobAsync(desired, claim.Target.KubernetesNamespace, cancellationToken = ct)
                                return false
                            with :? HttpOperationException as error when not (isNull error.Response) && error.Response.StatusCode = HttpStatusCode.Conflict -> return false
        }
