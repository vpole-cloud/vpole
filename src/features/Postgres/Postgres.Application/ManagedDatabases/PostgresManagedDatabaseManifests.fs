namespace Pole.Features.Postgres.Application.ManagedDatabases

open System
open System.Collections.Generic
open System.Globalization
open k8s.Models
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts

[<RequireQualifiedAccess>]
module PostgresManagedDatabaseManifests =
    [<Literal>]
    let ClaimLabel = "pole.io/postgres-database-claim-id"
    [<Literal>]
    let RevisionAnnotation = "pole.io/postgres-database-claim-revision"
    let resourceName (claim: PostgresManagedDatabase) = $"pole-db-{claim.Id}"
    let policyName (claim: PostgresManagedDatabase) = $"pole-pg-database-{claim.Id}"
    let clusterName (claim: PostgresManagedDatabase) = $"pg-{claim.ClusterId}"

    let labels (claim: PostgresManagedDatabase) =
        dict [ "pole.io/tenant-id", claim.TenantId
               "pole.io/runtime-cell-id", claim.Target.RuntimeCellId
               "pole.io/resource-deployment-id", claim.Target.DeploymentId
               "pole.io/postgres-cluster-id", claim.ClusterId
               ClaimLabel, claim.Id ]

    let podLabels (claim: PostgresManagedDatabase) =
        dict [ "pole.io/tenant-id", claim.TenantId
               "pole.io/runtime-cell-id", claim.Target.RuntimeCellId
               "pole.io/resource-deployment-id", claim.Target.DeploymentId ]

    let annotations (claim: PostgresManagedDatabase) =
        dict [ RevisionAnnotation, claim.Revision.ToString(CultureInfo.InvariantCulture) ]

    let private metadata (claim: PostgresManagedDatabase) clusterUid =
        {| name = resourceName claim
           ``namespace`` = claim.Target.KubernetesNamespace
           labels = labels claim
           annotations = annotations claim
           ownerReferences = [| {| apiVersion = "postgresql.cnpg.io/v1"; kind = "Cluster"
                                   name = clusterName claim; uid = clusterUid; blockOwnerDeletion = true |} |] |}

    let role (claim: PostgresManagedDatabase) credentialSecretName clusterUid =
        {| apiVersion = "postgresql.cnpg.io/v1"
           kind = "DatabaseRole"
           metadata = metadata claim clusterUid
           spec =
            {| cluster = {| name = clusterName claim |}
               name = claim.Username
               ensure = "present"
               login = true
               superuser = false
               createdb = false
               createrole = false
               replication = false
               bypassrls = false
               ``inherit`` = false
               inRoles = Array.empty<string>
               connectionLimit = -1
               passwordSecret = {| name = credentialSecretName |}
               databaseRoleReclaimPolicy = "delete" |} |}

    let database (claim: PostgresManagedDatabase) clusterUid =
        {| apiVersion = "postgresql.cnpg.io/v1"
           kind = "Database"
           metadata = metadata claim clusterUid
           spec =
            {| cluster = {| name = clusterName claim |}
               name = claim.Database
               owner = claim.Username
               ensure = "present"
               template = "template0"
               encoding = "UTF8"
               localeCollate = "C"
               localeCType = "C"
               databaseReclaimPolicy = "delete"
               schemas = [| {| name = "public"; owner = claim.Username; ensure = "present" |} |] |} |}

    let networkPolicy (claim: PostgresManagedDatabase) (sources: ResourceDeploymentRuntimePlacementDto array) =
        let peers = ResizeArray<V1NetworkPolicyPeer>()
        for source in sources do
            peers.Add(V1NetworkPolicyPeer(
                NamespaceSelector = V1LabelSelector(MatchLabels = dict [
                    "kubernetes.io/metadata.name", source.KubernetesNamespace
                    "pole.io/tenant-id", claim.TenantId
                    "pole.io/runtime-cell-id", source.RuntimeCellId ]),
                PodSelector = V1LabelSelector(MatchLabels = dict [
                    "pole.io/tenant-id", claim.TenantId
                    "pole.io/runtime-cell-id", source.RuntimeCellId
                    "pole.io/resource-deployment-id", source.DeploymentId
                    "pole.io/resource-id", claim.Owner.ResourceId
                    "app.kubernetes.io/component", claim.Source.Component ])))
        let ingress = V1NetworkPolicyIngressRule(
            Ports = ResizeArray [ V1NetworkPolicyPort(Protocol = "TCP", Port = IntOrString.op_Implicit(5432)) ],
            FromProperty = peers)
        V1NetworkPolicy(
            ApiVersion = "networking.k8s.io/v1", Kind = "NetworkPolicy",
            Metadata = V1ObjectMeta(Name = policyName claim, NamespaceProperty = claim.Target.KubernetesNamespace,
                                   Labels = labels claim, Annotations = annotations claim),
            Spec = V1NetworkPolicySpec(
                PolicyTypes = ResizeArray [ "Ingress" ],
                PodSelector = V1LabelSelector(MatchLabels = podLabels claim),
                Ingress = ResizeArray [ ingress ]))

    let owns (claim: PostgresManagedDatabase) (metadata: V1ObjectMeta) =
        not (isNull metadata) && metadata.NamespaceProperty = claim.Target.KubernetesNamespace &&
        not (isNull metadata.Labels) &&
        (labels claim |> Seq.forall(fun (KeyValue(key, value)) ->
            match metadata.Labels.TryGetValue key with true, actual -> actual = value | _ -> false))
