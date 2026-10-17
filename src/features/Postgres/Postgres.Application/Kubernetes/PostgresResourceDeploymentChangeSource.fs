namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Collections.Generic
open System.Linq
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open k8s
open k8s.Models
open Pole.Features.Core.Kubernetes
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Persistence.Reads
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Observation

type PostgresResourceDeploymentChangeSource
    (
        loadCluster: LoadPostgresClusterById,
        clients: IKubernetesClientProvider,
        lookupTarget: LookupDeploymentTarget,
        budget: IObservationReadBudget,
        clock: TimeProvider
    ) =

    member private _.CurrentClientAsync(context: ResourceDeploymentObservationContext, ct: CancellationToken) =
        task {
            let targetId = context.Deployment.Placement.DeploymentTargetId

            let check (outcome: LookupDeploymentTargetOutcome) =
                match outcome.Value with
                | :? DeploymentTargetLookupDto as target when
                    target.Enabled
                    && not target.DeletionRequested
                    && target.ConnectionVersion = context.TargetConnectionVersion
                    ->
                    ()
                | _ -> raise (HttpRequestException("ObservationTargetChanged"))

            let! before = lookupTarget.Invoke(targetId, ct)
            check before

            let! client =
                task {
                    try
                        return! clients.GetAsync(targetId, ct)
                    with :? DeploymentTargetUnavailableException ->
                        return raise (HttpRequestException("ObservationTargetUnavailable"))
                }

            let! after = lookupTarget.Invoke(targetId, ct)
            check after
            return client
        }

    member private this.ReadAsync<'T>(context, read: IKubernetes -> CancellationToken -> Task<'T>, ct) =
        budget.ReadAsync<'T>(
            Func<CancellationToken, Task<'T>>(fun token ->
                task {
                    let! client = this.CurrentClientAsync(context, token)
                    return! read client token
                }),
            ct
        )

    member private this.WatchNative<'T>
        (context, ct, watch: Func<IKubernetes, IAsyncEnumerable<struct (WatchEventType * 'T)>>)
        =
        KubernetesListWatch.WithClientAsync<'T>(
            Func<CancellationToken, Task<IKubernetes>>(fun token -> this.CurrentClientAsync(context, token)),
            watch,
            ct
        )

    member private _.Interest<'T>
        (
            key,
            singleton,
            list: Func<string, CancellationToken, Task<KubernetesListPage<'T>>>,
            watch: Func<string, Action<Exception>, CancellationToken, IAsyncEnumerable<struct (WatchEventType * 'T)>>,
            metadata: Func<'T, V1ObjectMeta>,
            project: Func<'T, KubernetesOwnedWatchItem>
        ) =
        KubernetesOwnedWatchInterest.Create<'T>(
            key,
            singleton,
            list,
            watch,
            Func<'T, string>(fun item -> metadata.Invoke(item).ResourceVersion),
            project,
            clock
        )

    member private this.CreateSessionAsync(context: ResourceDeploymentObservationContext, ct: CancellationToken) =
        task {
            let deployment = context.Deployment

            let! cluster =
                if deployment.ResourceType = PostgresResourceDeployment.ResourceType then
                    loadCluster.Invoke(deployment.TenantId, deployment.ResourceId, ct)
                else
                    Task.FromResult(None)

            match cluster with
            | None -> return null
            | Some cluster ->
                let identity =
                    WorkloadIdentityLabels(
                        deployment.TenantId,
                        deployment.ResourceGroupId,
                        deployment.IsolationScopeId,
                        deployment.RuntimeCellId,
                        deployment.Placement.DeploymentTargetId,
                        "Postgres",
                        deployment.ResourceId,
                        deployment.Id
                    )

                let ns = deployment.Placement.KubernetesNamespace
                let name = PostgresClusterKubernetesBuilder.buildResourceName cluster.Id

                let selector =
                    String.Join(',', identity.ToDictionary().Select(fun pair -> $"{pair.Key}={pair.Value}"))

                let owns (meta: V1ObjectMeta) exactName =
                    not (isNull meta)
                    && meta.NamespaceProperty = ns
                    && (exactName |> Option.forall (fun name -> meta.Name = name))
                    && KubernetesWorkloadObservation.Owns(meta, identity)

                let project meta owned =
                    KubernetesOwnedWatchItem.FromMetadata(meta, owned)

                let operator = cluster.Spec.DeploymentMode = PostgresDeploymentMode.Operator

                let ownerKind, ownerApi =
                    if operator then
                        "Cluster", "postgresql.cnpg.io/v1"
                    else
                        "StatefulSet", "apps/v1"

                let controller =
                    if operator then
                        this.Interest<obj>(
                            "controller",
                            true,
                            Func<_, _, _>(fun continuation token ->
                                task {
                                    let! page =
                                        this.ReadAsync(
                                            context,
                                            (fun client token ->
                                                client.CustomObjects.ListNamespacedCustomObjectAsync<JsonElement>(
                                                    "postgresql.cnpg.io",
                                                    "v1",
                                                    ns,
                                                    "clusters",
                                                    fieldSelector = $"metadata.name={name}",
                                                    labelSelector = selector,
                                                    limit = 100,
                                                    continueParameter = continuation,
                                                    cancellationToken = token
                                                )),
                                            token
                                        )

                                    let meta =
                                        KubernetesJson.Deserialize<V1ListMeta>(
                                            page.GetProperty("metadata").GetRawText()
                                        )

                                    return
                                        KubernetesListPage<obj>(
                                            page
                                                .GetProperty("items")
                                                .EnumerateArray()
                                                .Select(fun item -> box (item.Clone()))
                                                .ToArray(),
                                            meta.ResourceVersion,
                                            meta.ContinueProperty
                                        )
                                }),
                            Func<_, _, _, _>(fun version error token ->
                                this.WatchNative(
                                    context,
                                    token,
                                    Func<_, _>(fun client ->
                                        client.CustomObjects.WatchListNamespacedCustomObjectAsync(
                                            "postgresql.cnpg.io",
                                            "v1",
                                            ns,
                                            "clusters",
                                            allowWatchBookmarks = true,
                                            fieldSelector = $"metadata.name={name}",
                                            labelSelector = selector,
                                            resourceVersion = version,
                                            timeoutSeconds = 300,
                                            onError = error,
                                            cancellationToken = token
                                        ))
                                )),
                            Func<_, _>(PostgresClusterObservation.metadata),
                            Func<_, _>(fun item ->
                                let meta = PostgresClusterObservation.metadata item in
                                project meta (owns meta (Some name)))
                        )
                    else
                        this.Interest<V1StatefulSet>(
                            "controller",
                            true,
                            Func<_, _, _>(fun continuation token ->
                                task {
                                    let! page =
                                        this.ReadAsync(
                                            context,
                                            (fun client token ->
                                                client.AppsV1.ListNamespacedStatefulSetAsync(
                                                    ns,
                                                    fieldSelector = $"metadata.name={name}",
                                                    labelSelector = selector,
                                                    limit = 100,
                                                    continueParameter = continuation,
                                                    cancellationToken = token
                                                )),
                                            token
                                        )

                                    return
                                        KubernetesListPage<V1StatefulSet>(
                                            page.Items.ToArray(),
                                            page.Metadata.ResourceVersion,
                                            page.Metadata.ContinueProperty
                                        )
                                }),
                            Func<_, _, _, _>(fun version error token ->
                                this.WatchNative(
                                    context,
                                    token,
                                    Func<_, _>(fun client ->
                                        client.AppsV1.WatchListNamespacedStatefulSetAsync(
                                            ns,
                                            allowWatchBookmarks = true,
                                            fieldSelector = $"metadata.name={name}",
                                            labelSelector = selector,
                                            resourceVersion = version,
                                            timeoutSeconds = 300,
                                            onError = error,
                                            cancellationToken = token
                                        ))
                                )),
                            Func<_, _>(fun item -> item.Metadata),
                            Func<_, _>(fun item -> project item.Metadata (owns item.Metadata (Some name)))
                        )

                let dependents binding (graph: IReadOnlyDictionary<string, IReadOnlyList<KubernetesOwnedWatchItem>>) =
                    let items key =
                        match graph.TryGetValue(key) with
                        | true, values -> values :> seq<_>
                        | _ -> Seq.empty

                    let result = ResizeArray<KubernetesOwnedWatchInterest>()

                    result.Add(
                        this.Interest<V1Pod>(
                            "pods",
                            false,
                            Func<_, _, _>(fun continuation token ->
                                task {
                                    let! page =
                                        this.ReadAsync(
                                            context,
                                            (fun client token ->
                                                client.CoreV1.ListNamespacedPodAsync(
                                                    ns,
                                                    labelSelector = selector,
                                                    limit = 100,
                                                    continueParameter = continuation,
                                                    cancellationToken = token
                                                )),
                                            token
                                        )

                                    return
                                        KubernetesListPage<V1Pod>(
                                            page.Items.ToArray(),
                                            page.Metadata.ResourceVersion,
                                            page.Metadata.ContinueProperty
                                        )
                                }),
                            Func<_, _, _, _>(fun version error token ->
                                this.WatchNative(
                                    context,
                                    token,
                                    Func<_, _>(fun client ->
                                        client.CoreV1.WatchListNamespacedPodAsync(
                                            ns,
                                            allowWatchBookmarks = true,
                                            labelSelector = selector,
                                            resourceVersion = version,
                                            timeoutSeconds = 300,
                                            onError = error,
                                            cancellationToken = token
                                        ))
                                )),
                            Func<_, _>(fun item -> item.Metadata),
                            Func<_, _>(fun item ->
                                project
                                    item.Metadata
                                    (owns item.Metadata None
                                     && KubernetesOwnedWatchItem.ControlledBy(
                                         item.Metadata,
                                         binding,
                                         ownerKind,
                                         ownerApi
                                     )))
                        )
                    )

                    let services =
                        if operator then
                            [ PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id ]
                        else
                            [ PostgresStatefulSetKubernetesBuilder.buildHeadlessServiceName cluster.Id
                              PostgresClusterKubernetesBuilder.buildReadWriteServiceName cluster.Id ]

                    for serviceName in services do
                        let serviceKey = "service:" + serviceName

                        result.Add(
                            this.Interest<V1Service>(
                                serviceKey,
                                true,
                                Func<_, _, _>(fun continuation token ->
                                    task {
                                        let! page =
                                            this.ReadAsync(
                                                context,
                                                (fun client token ->
                                                    client.CoreV1.ListNamespacedServiceAsync(
                                                        ns,
                                                        fieldSelector = $"metadata.name={serviceName}",
                                                        labelSelector = selector,
                                                        limit = 100,
                                                        continueParameter = continuation,
                                                        cancellationToken = token
                                                    )),
                                                token
                                            )

                                        return
                                            KubernetesListPage<V1Service>(
                                                page.Items.ToArray(),
                                                page.Metadata.ResourceVersion,
                                                page.Metadata.ContinueProperty
                                            )
                                    }),
                                Func<_, _, _, _>(fun version error token ->
                                    this.WatchNative(
                                        context,
                                        token,
                                        Func<_, _>(fun client ->
                                            client.CoreV1.WatchListNamespacedServiceAsync(
                                                ns,
                                                allowWatchBookmarks = true,
                                                fieldSelector = $"metadata.name={serviceName}",
                                                labelSelector = selector,
                                                resourceVersion = version,
                                                timeoutSeconds = 300,
                                                onError = error,
                                                cancellationToken = token
                                            ))
                                    )),
                                Func<_, _>(fun item -> item.Metadata),
                                Func<_, _>(fun item ->
                                    project
                                        item.Metadata
                                        (owns item.Metadata (Some serviceName)
                                         && (not operator
                                             || KubernetesOwnedWatchItem.ControlledBy(
                                                 item.Metadata,
                                                 binding,
                                                 "Cluster",
                                                 "postgresql.cnpg.io/v1"
                                             ))))
                            )
                        )

                        for service in items serviceKey do
                            result.Add(
                                this.Interest<V1EndpointSlice>(
                                    "slice:" + service.Uid,
                                    false,
                                    Func<_, _, _>(fun continuation token ->
                                        task {
                                            let! page =
                                                this.ReadAsync(
                                                    context,
                                                    (fun client token ->
                                                        client.DiscoveryV1.ListNamespacedEndpointSliceAsync(
                                                            ns,
                                                            labelSelector =
                                                                $"kubernetes.io/service-name={serviceName}",
                                                            limit = 100,
                                                            continueParameter = continuation,
                                                            cancellationToken = token
                                                        )),
                                                    token
                                                )

                                            return
                                                KubernetesListPage<V1EndpointSlice>(
                                                    page.Items.ToArray(),
                                                    page.Metadata.ResourceVersion,
                                                    page.Metadata.ContinueProperty
                                                )
                                        }),
                                    Func<_, _, _, _>(fun version error token ->
                                        this.WatchNative(
                                            context,
                                            token,
                                            Func<_, _>(fun client ->
                                                client.DiscoveryV1.WatchListNamespacedEndpointSliceAsync(
                                                    ns,
                                                    allowWatchBookmarks = true,
                                                    labelSelector = $"kubernetes.io/service-name={serviceName}",
                                                    resourceVersion = version,
                                                    timeoutSeconds = 300,
                                                    onError = error,
                                                    cancellationToken = token
                                                ))
                                        )),
                                    Func<_, _>(fun item -> item.Metadata),
                                    Func<_, _>(fun item ->
                                        let mutable serviceLabel = null

                                        project
                                            item.Metadata
                                            (not (isNull item.Metadata)
                                             && item.Metadata.NamespaceProperty = ns
                                             && not (String.IsNullOrEmpty(item.Metadata.Uid))
                                             && not (isNull item.Metadata.Labels)
                                             && item.Metadata.Labels.TryGetValue(
                                                 "kubernetes.io/service-name",
                                                 &serviceLabel
                                             )
                                             && serviceLabel = serviceName
                                             && KubernetesOwnedWatchItem.ControlledBy(
                                                 item.Metadata,
                                                 service.Uid,
                                                 "Service",
                                                 "v1"
                                             )))
                                )
                            )

                    let uids =
                        Seq.append
                            [ binding ]
                            (items "pods"
                             |> Seq.map (fun item -> item.Uid)
                             |> Seq.sortWith (fun first second -> StringComparer.Ordinal.Compare(first, second)))
                        |> Seq.distinct
                        |> Seq.truncate 8

                    for uid in uids do
                        result.Add(
                            this.Interest<Corev1Event>(
                                "warning:" + uid,
                                false,
                                Func<_, _, _>(fun continuation token ->
                                    task {
                                        let! page =
                                            this.ReadAsync(
                                                context,
                                                (fun client token ->
                                                    client.CoreV1.ListNamespacedEventAsync(
                                                        ns,
                                                        fieldSelector = $"involvedObject.uid={uid},type=Warning",
                                                        limit = 100,
                                                        continueParameter = continuation,
                                                        cancellationToken = token
                                                    )),
                                                token
                                            )

                                        return
                                            KubernetesListPage<Corev1Event>(
                                                page.Items.ToArray(),
                                                page.Metadata.ResourceVersion,
                                                page.Metadata.ContinueProperty
                                            )
                                    }),
                                Func<_, _, _, _>(fun version error token ->
                                    this.WatchNative(
                                        context,
                                        token,
                                        Func<_, _>(fun client ->
                                            client.CoreV1.WatchListNamespacedEventAsync(
                                                ns,
                                                allowWatchBookmarks = true,
                                                fieldSelector = $"involvedObject.uid={uid},type=Warning",
                                                resourceVersion = version,
                                                timeoutSeconds = 300,
                                                onError = error,
                                                cancellationToken = token
                                            ))
                                    )),
                                Func<_, _>(fun item -> item.Metadata),
                                Func<_, _>(fun item ->
                                    project
                                        item.Metadata
                                        (not (isNull item.Metadata)
                                         && item.Metadata.NamespaceProperty = ns
                                         && not (String.IsNullOrEmpty(item.Metadata.Uid))
                                         && item.Type = "Warning"
                                         && not (isNull item.InvolvedObject)
                                         && item.InvolvedObject.Uid = uid))
                            )
                        )

                    result :> IReadOnlyList<_>

                return KubernetesOwnedWatchSession(controller, Func<_, _, _>(dependents), clock, ct)
        }

    interface IResourceDeploymentObservationChangeSource with
        member this.WatchAsync(context, ct) =
            AsyncEnumerable.Select<KubernetesOwnedWatchChange, ResourceDeploymentObservationChange>(
                KubernetesOwnedWatchSession.StartAsync(
                    Func<_, _>(fun token -> this.CreateSessionAsync(context, token)),
                    ct
                ),
                Func<KubernetesOwnedWatchChange, ResourceDeploymentObservationChange>(fun change ->
                    let kind =
                        match change.Kind with
                        | KubernetesOwnedWatchChangeKind.Ready -> ResourceDeploymentObservationChangeKind.SourceReady
                        | KubernetesOwnedWatchChangeKind.Invalidated ->
                            ResourceDeploymentObservationChangeKind.Invalidated
                        | KubernetesOwnedWatchChangeKind.BindingChanged ->
                            ResourceDeploymentObservationChangeKind.BindingChanged
                        | KubernetesOwnedWatchChangeKind.Unavailable ->
                            ResourceDeploymentObservationChangeKind.Unavailable
                        | _ -> invalidOp "UnknownNativeChange"

                    ResourceDeploymentObservationChange(kind, change.BindingId))
            )
