namespace Pole.Features.Postgres.Application.Kubernetes

open System.Collections.Generic
open k8s.Models
open Pole.Features.Postgres.Domain
open Pole.Features.Postgres.Application.Common

[<RequireQualifiedAccess>]
module PostgresStatefulSetKubernetesBuilder =
    let buildHeadlessServiceName (clusterId : string) = $"{clusterId}-headless"

    let private buildSecretKeyReference secretName key =
        V1EnvVarSource(SecretKeyRef = V1SecretKeySelector(Name = secretName, Key = key))

    let private buildStorageRequests (cluster : PostgresCluster) =
        let requests = Dictionary<string, ResourceQuantity>()
        requests["storage"] <- ResourceQuantity($"{cluster.Spec.Storage}Gi")
        requests

    let private buildComputeResources (cluster : PostgresCluster) =
        let requests = Dictionary<string, ResourceQuantity>()
        requests["cpu"] <- ResourceQuantity($"{cluster.Spec.Resources.Requests.CpuCores}")
        requests["memory"] <- ResourceQuantity($"{cluster.Spec.Resources.Requests.MemoryMi}Mi")

        let limits = Dictionary<string, ResourceQuantity>()
        limits["cpu"] <- ResourceQuantity($"{cluster.Spec.Resources.Limits.CpuCores}")
        limits["memory"] <- ResourceQuantity($"{cluster.Spec.Resources.Limits.MemoryMi}Mi")

        V1ResourceRequirements(Requests = requests, Limits = limits)

    let buildStatefulSet
        (cluster : PostgresCluster)
        (namespaceName : string)
        (storageClassName : string)
        (metadata : PostgresResourceDeploymentMetadata)
        (settings : PostgresSettings) =
        let labels = PostgresClusterKubernetesBuilder.buildLabels cluster metadata
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
                                                  Resources = buildComputeResources cluster,
                                                  VolumeMounts = ResizeArray([ V1VolumeMount(Name = "postgres-data", MountPath = "/var/lib/postgresql/data") ])) ]))),
                    VolumeClaimTemplates =
                        ResizeArray(
                            [ V1PersistentVolumeClaim(
                                  Metadata = V1ObjectMeta(Name = "postgres-data", Labels = labels),
                                  Spec =
                                      V1PersistentVolumeClaimSpec(
                                          AccessModes = ResizeArray([ "ReadWriteOnce" ]),
                                          StorageClassName = storageClassName,
                                          Resources =
                                              V1VolumeResourceRequirements(Requests = buildStorageRequests cluster))) ])))

    let buildHeadlessService
        (cluster : PostgresCluster)
        (namespaceName : string)
        (metadata : PostgresResourceDeploymentMetadata) =
        let labels = PostgresClusterKubernetesBuilder.buildLabels cluster metadata

        V1Service(
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = V1ObjectMeta(Name = buildHeadlessServiceName cluster.Id, NamespaceProperty = namespaceName, Labels = labels),
            Spec =
                V1ServiceSpec(
                    Type = "ClusterIP",
                    ClusterIP = "None",
                    Selector = labels,
                    Ports = ResizeArray([ V1ServicePort(Name = "postgres", Port = 5432, Protocol = "TCP") ])))

    let buildReadWriteService
        (cluster : PostgresCluster)
        (namespaceName : string)
        (metadata : PostgresResourceDeploymentMetadata) =
        let labels = PostgresClusterKubernetesBuilder.buildLabels cluster metadata

        V1Service(
            ApiVersion = "v1",
            Kind = "Service",
            Metadata =
                V1ObjectMeta(
                    Name = PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id,
                    NamespaceProperty = namespaceName,
                    Labels = labels),
            Spec =
                V1ServiceSpec(
                    Type = "ClusterIP",
                    Selector = labels,
                    Ports = ResizeArray([ V1ServicePort(Name = "postgres", Port = 5432, Protocol = "TCP") ])))
