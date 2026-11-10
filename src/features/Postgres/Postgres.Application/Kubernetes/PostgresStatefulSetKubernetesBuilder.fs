namespace Pole.Features.Postgres.Application.Kubernetes

open System.Collections.Generic
open k8s.Models
open Pole.Features.Core.Kubernetes
open Pole.Features.Postgres.Domain
open Pole.Features.Secrets.Contracts
open Pole.Features.Postgres.Application.Common

[<RequireQualifiedAccess>]
module PostgresStatefulSetKubernetesBuilder =
    let buildHeadlessServiceName (clusterId: string) =
        $"{PostgresClusterKubernetesBuilder.buildResourceName clusterId}-headless"

    let private buildSecretKeyReference secretName key =
        V1EnvVarSource(SecretKeyRef = V1SecretKeySelector(Name = secretName, Key = key))

    let private buildStorageRequests (cluster: PostgresCluster) =
        let requests = Dictionary<string, ResourceQuantity>()
        requests["storage"] <- ResourceQuantity($"{cluster.Spec.Storage}Gi")
        requests

    let buildStatefulSet
        (cluster: PostgresCluster)
        (projection: SecretProjectionReference)
        (namespaceName: string)
        (storageClassName: string)
        (compute: KubernetesComputeConfiguration)
        (metadata: PostgresResourceDeploymentMetadata)
        (settings: PostgresSettings)
        =
        cluster.EnsureConfigured()
        let labels = PostgresClusterKubernetesBuilder.buildLabels cluster metadata
        let annotations = PostgresClusterKubernetesBuilder.buildAnnotations ()
        let secretName = projection.KubernetesName
        let databaseName = PostgresClusterKubernetesBuilder.buildDatabaseName cluster.Id

        let image =
            PostgresSettings.buildImageReference settings.StatefulSetImage cluster.Spec.Version

        let podSpec =
            V1PodSpec(
                Containers =
                    ResizeArray(
                        [ V1Container(
                              Name = "postgres",
                              Image = image,
                              Ports = ResizeArray([ V1ContainerPort(ContainerPort = 5432, Name = "postgres") ]),
                              Env =
                                  ResizeArray(
                                      [ V1EnvVar(
                                            Name = "POSTGRES_USER",
                                            ValueFrom = buildSecretKeyReference secretName "username"
                                        )
                                        V1EnvVar(
                                            Name = "POSTGRES_PASSWORD",
                                            ValueFrom = buildSecretKeyReference secretName "password"
                                        )
                                        V1EnvVar(Name = "POSTGRES_DB", Value = databaseName) ]
                                  ),
                              Resources = compute.ToResourceRequirements(),
                              VolumeMounts =
                                  ResizeArray(
                                      [ V1VolumeMount(Name = "postgres-data", MountPath = "/var/lib/postgresql/data") ]
                                  )
                          ) ]
                    )
            )

        compute.ApplyTo(podSpec)

        V1StatefulSet(
            ApiVersion = "apps/v1",
            Kind = "StatefulSet",
            Metadata =
                V1ObjectMeta(
                    Name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id,
                    NamespaceProperty = namespaceName,
                    Labels = labels,
                    Annotations = annotations
                ),
            Spec =
                V1StatefulSetSpec(
                    ServiceName = buildHeadlessServiceName cluster.Id,
                    Replicas = 1,
                    Selector =
                        V1LabelSelector(MatchLabels = dict [ "pole.io/resource-deployment-id", metadata.DeploymentId ]),
                    Template =
                        V1PodTemplateSpec(
                            Metadata = V1ObjectMeta(Labels = labels, Annotations = annotations),
                            Spec = podSpec
                        ),
                    VolumeClaimTemplates =
                        ResizeArray(
                            [ V1PersistentVolumeClaim(
                                  Metadata =
                                      V1ObjectMeta(Name = "postgres-data", Labels = labels, Annotations = annotations),
                                  Spec =
                                      V1PersistentVolumeClaimSpec(
                                          AccessModes = ResizeArray([ "ReadWriteOnce" ]),
                                          StorageClassName = storageClassName,
                                          Resources =
                                              V1VolumeResourceRequirements(Requests = buildStorageRequests cluster)
                                      )
                              ) ]
                        )
                )
        )

    let buildHeadlessService
        (cluster: PostgresCluster)
        (namespaceName: string)
        (metadata: PostgresResourceDeploymentMetadata)
        =
        let labels = PostgresClusterKubernetesBuilder.buildLabels cluster metadata
        let annotations = PostgresClusterKubernetesBuilder.buildAnnotations ()

        V1Service(
            ApiVersion = "v1",
            Kind = "Service",
            Metadata =
                V1ObjectMeta(
                    Name = buildHeadlessServiceName cluster.Id,
                    NamespaceProperty = namespaceName,
                    Labels = labels,
                    Annotations = annotations
                ),
            Spec =
                V1ServiceSpec(
                    Type = "ClusterIP",
                    ClusterIP = "None",
                    Selector = dict [ "pole.io/resource-deployment-id", metadata.DeploymentId ],
                    Ports = ResizeArray([ V1ServicePort(Name = "postgres", Port = 5432, Protocol = "TCP") ])
                )
        )

    let buildReadWriteService
        (cluster: PostgresCluster)
        (namespaceName: string)
        (metadata: PostgresResourceDeploymentMetadata)
        =
        let labels = PostgresClusterKubernetesBuilder.buildLabels cluster metadata
        let annotations = PostgresClusterKubernetesBuilder.buildAnnotations ()

        V1Service(
            ApiVersion = "v1",
            Kind = "Service",
            Metadata =
                V1ObjectMeta(
                    Name = PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id,
                    NamespaceProperty = namespaceName,
                    Labels = labels,
                    Annotations = annotations
                ),
            Spec =
                V1ServiceSpec(
                    Type = "ClusterIP",
                    Selector = dict [ "pole.io/resource-deployment-id", metadata.DeploymentId ],
                    Ports = ResizeArray([ V1ServicePort(Name = "postgres", Port = 5432, Protocol = "TCP") ])
                )
        )
