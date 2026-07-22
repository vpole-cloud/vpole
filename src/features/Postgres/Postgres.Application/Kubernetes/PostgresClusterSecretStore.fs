namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Pole.Features.Core.Kubernetes
open Pole.Features.DeploymentTargets.Contracts
open k8s
open k8s.Autorest
open k8s.Models

type PostgresClusterCredentials =
    { Username : string
      Password : string }

type IPostgresClusterSecretStore =
    abstract member CreateAsync :
        namespaceName : string *
        clusterId : string *
        username : string *
        password : string *
        cancellationToken : CancellationToken -> Task
    abstract member UpdatePasswordAsync :
        namespaceName : string *
        clusterId : string *
        password : string *
        cancellationToken : CancellationToken -> Task
    abstract member GetAsync :
        namespaceName : string *
        clusterId : string *
        cancellationToken : CancellationToken -> Task<PostgresClusterCredentials option>

type KubernetesPostgresClusterSecretStore(kubernetesClientProvider : IKubernetesClientProvider) =
    let tryGetStringData (secret : V1Secret) key =
        match secret.StringData with
        | null -> None
        | data ->
            match data.TryGetValue(key) with
            | true, value when not (String.IsNullOrWhiteSpace(value)) -> Some value
            | _ -> None

    let tryGetBinaryData (secret : V1Secret) key =
        match secret.Data with
        | null -> None
        | data ->
            match data.TryGetValue(key) with
            | true, value when not (isNull value) -> Some(Encoding.UTF8.GetString(value))
            | _ -> None

    let toCredentials (secret : V1Secret) =
        let username = tryGetStringData secret "username" |> Option.orElseWith (fun () -> tryGetBinaryData secret "username")
        let password = tryGetStringData secret "password" |> Option.orElseWith (fun () -> tryGetBinaryData secret "password")

        match username, password with
        | Some username, Some password -> Some { Username = username; Password = password }
        | _ -> None

    interface IPostgresClusterSecretStore with
        member _.CreateAsync(namespaceName, clusterId, username, password, cancellationToken) =
            task {
                let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                let secret = V1Secret()
                secret.ApiVersion <- "v1"
                secret.Kind <- "Secret"
                secret.Metadata <- V1ObjectMeta(Name = PostgresClusterKubernetesBuilder.buildSecretName clusterId, NamespaceProperty = namespaceName)
                secret.StringData <- dict [ "username", username; "password", password ]

                do!
                    KubernetesExtensions.CreateIfNotExistAsync(fun () ->
                        kubernetes.CoreV1.CreateNamespacedSecretAsync(secret, namespaceName, cancellationToken = cancellationToken))
            }

        member _.UpdatePasswordAsync(namespaceName, clusterId, password, cancellationToken) =
            task {
                let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                let secretName = PostgresClusterKubernetesBuilder.buildSecretName clusterId
                let! secret = kubernetes.CoreV1.ReadNamespacedSecretAsync(secretName, namespaceName, cancellationToken = cancellationToken)

                let updatedSecret = V1Secret()
                updatedSecret.ApiVersion <- secret.ApiVersion
                updatedSecret.Kind <- secret.Kind
                updatedSecret.Metadata <- secret.Metadata
                updatedSecret.Type <- secret.Type
                updatedSecret.Immutable <- secret.Immutable
                updatedSecret.StringData <-
                    dict [
                        "username",
                        (toCredentials secret
                         |> Option.map _.Username
                         |> Option.defaultWith (fun () -> invalidOp $"Secret '{secretName}' does not contain a username."))
                        "password", password
                    ]

                do!
                    kubernetes.CoreV1.ReplaceNamespacedSecretAsync(
                        updatedSecret,
                        secretName,
                        namespaceName,
                        cancellationToken = cancellationToken)
                    :> Task
            }

        member _.GetAsync(namespaceName, clusterId, cancellationToken) =
            task {
                try
                    let! kubernetes = kubernetesClientProvider.GetDefaultAsync(cancellationToken)
                    let secretName = PostgresClusterKubernetesBuilder.buildSecretName clusterId
                    let! secret = kubernetes.CoreV1.ReadNamespacedSecretAsync(secretName, namespaceName, cancellationToken = cancellationToken)
                    return toCredentials secret
                with
                | :? HttpOperationException as ex when not (isNull ex.Response) && ex.Response.StatusCode = Net.HttpStatusCode.NotFound ->
                    return None
            }

