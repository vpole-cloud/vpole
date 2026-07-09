namespace Pole.Features.Postgres.Application.Kubernetes

open System.Collections.Generic
open k8s.Models
open Pole.Features.Shared.Models
open Pole.Features.StorageProfiles.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common

[<RequireQualifiedAccess>]
module PostgresStatefulSetKubernetesBuilder =
    let buildHeadlessServiceName (clusterId : string) = $"{clusterId}-headless"

    let private buildLabels (cluster : PostgresCluster) =
        dict [
            "app", cluster.Id
            "component", "postgres"
            PoleStorageLabels.TenantId, cluster.TenantId
            PoleStorageLabels.ResourceGroupId, cluster.GroupId
            PoleStorageLabels.ResourceType, ResourceType.Postgres.ToString()
            PoleStorageLabels.ResourceId, cluster.Id
            PoleStorageLabels.ResourceName, cluster.Name
        ]

    let private buildSecretKeyReference secretName key =
        V1EnvVarSource(SecretKeyRef = V1SecretKeySelector(Name = secretName, Key = key))

    let private buildStorageRequests (cluster : PostgresCluster) =
        let requests = Dictionary<string, ResourceQuantity>()
        requests["storage"] <- ResourceQuantity($"{cluster.Spec.Storage}Gi")
        requests

    let buildStatefulSet (cluster : PostgresCluster) (settings : PostgresSettings) =
        let labels = buildLabels cluster
        let namespaceName = cluster.Spec.KubernetesNamespace
        let secretName = PostgresClusterKubernetesBuilder.buildSecretName cluster.Id
        let databaseName = PostgresClusterKubernetesBuilder.buildDatabaseName cluster.Id
        let image = PostgresSettings.buildImageReference settings.StatefulSetImage cluster.Spec.Version

        V1StatefulSet(
            ApiVersion = "apps/v1",
            Kind = "StatefulSet",
            Metadata = V1ObjectMeta(Name = cluster.Id, NamespaceProperty = namespaceName, Labels = labels),
            Spec =
                V1StatefulSetSpec(
                    ServiceName = buildHeadlessServiceName cluster.Id,
                    Replicas = 1,
                    Selector = V1LabelSelector(MatchLabels = labels),
                    Template =
                        V1PodTemplateSpec(
                            Metadata = V1ObjectMeta(Labels = labels),
                            Spec =
                                V1PodSpec(
                                    Containers =
                                        ResizeArray(
                                            [ V1Container(
                                                  Name = "postgres",
                                                  Image = image,
                                                  Ports = ResizeArray([ V1ContainerPort(ContainerPort = 5432, Name = "postgres") ]),
                                                  Env =
                                                      ResizeArray(
                                                          [ V1EnvVar(Name = "POSTGRES_USER", ValueFrom = buildSecretKeyReference secretName "username")
                                                            V1EnvVar(Name = "POSTGRES_PASSWORD", ValueFrom = buildSecretKeyReference secretName "password")
                                                            V1EnvVar(Name = "POSTGRES_DB", Value = databaseName) ]),
                                                  VolumeMounts = ResizeArray([ V1VolumeMount(Name = "postgres-data", MountPath = "/var/lib/postgresql/data") ])) ]))),
                    VolumeClaimTemplates =
                        ResizeArray(
                            [ V1PersistentVolumeClaim(
                                  Metadata = V1ObjectMeta(Name = "postgres-data", Labels = labels),
                                  Spec =
                                      V1PersistentVolumeClaimSpec(
                                          AccessModes = ResizeArray([ "ReadWriteOnce" ]),
                                          StorageClassName = cluster.Spec.StorageProfile.StorageClassName,
                                          Resources =
                                              V1VolumeResourceRequirements(Requests = buildStorageRequests cluster))) ])))

    let buildHeadlessService (cluster : PostgresCluster) =
        let labels = buildLabels cluster

        V1Service(
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = V1ObjectMeta(Name = buildHeadlessServiceName cluster.Id, NamespaceProperty = cluster.Spec.KubernetesNamespace, Labels = labels),
            Spec =
                V1ServiceSpec(
                    Type = "ClusterIP",
                    ClusterIP = "None",
                    Selector = labels,
                    Ports = ResizeArray([ V1ServicePort(Name = "postgres", Port = 5432, Protocol = "TCP") ])))

    let buildReadWriteService (cluster : PostgresCluster) =
        let labels = buildLabels cluster

        V1Service(
            ApiVersion = "v1",
            Kind = "Service",
            Metadata =
                V1ObjectMeta(
                    Name = PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id,
                    NamespaceProperty = cluster.Spec.KubernetesNamespace,
                    Labels = labels),
            Spec =
                V1ServiceSpec(
                    Type = "ClusterIP",
                    Selector = labels,
                    Ports = ResizeArray([ V1ServicePort(Name = "postgres", Port = 5432, Protocol = "TCP") ])))

