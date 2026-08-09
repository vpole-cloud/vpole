namespace Pole.Features.Postgres.Application.Common

open System.Collections.Immutable
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.ComputeProfiles.Primitives
open Pole.Features.Core.Kubernetes
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.StorageProfiles.Contracts
open Pole.Workloads

[<AbstractClass; Sealed>]
type PostgresResourceDeployment =
    static member ResourceType = ResourceTypeId.Create("pole/postgresql")

[<RequireQualifiedAccess>]
module PostgresResourceDeploymentMappings =
    let private affinityOperator value =
        match value with
        | ComputeNodeAffinityOperator.In -> ResourceDeploymentNodeAffinityOperator.In
        | ComputeNodeAffinityOperator.NotIn -> ResourceDeploymentNodeAffinityOperator.NotIn
        | ComputeNodeAffinityOperator.Exists -> ResourceDeploymentNodeAffinityOperator.Exists
        | ComputeNodeAffinityOperator.DoesNotExist -> ResourceDeploymentNodeAffinityOperator.DoesNotExist
        | ComputeNodeAffinityOperator.GreaterThan -> ResourceDeploymentNodeAffinityOperator.GreaterThan
        | ComputeNodeAffinityOperator.LessThan -> ResourceDeploymentNodeAffinityOperator.LessThan
        | _ -> invalidArg (nameof value) "Unsupported node-affinity operator."

    let private affinityTerm (term : ComputeNodeAffinityTermValue) =
        term.Expressions
        |> Seq.map (fun expression ->
            ResourceDeploymentNodeAffinityExpression(
                expression.Key,
                affinityOperator expression.Operator,
                expression.Values))
        |> ImmutableList.CreateRange
        |> ResourceDeploymentNodeAffinityTerm

    let compute (profile : ResolvedComputeProfile) =
        let architecture =
            match profile.Architecture with
            | ComputeArchitecture.Any -> ResourceDeploymentComputeArchitecture.Any
            | ComputeArchitecture.Amd64 -> ResourceDeploymentComputeArchitecture.Amd64
            | ComputeArchitecture.Arm64 -> ResourceDeploymentComputeArchitecture.Arm64
            | _ -> invalidArg (nameof profile) "Unsupported compute architecture."

        let tolerationOperator value =
            match value with
            | ComputeTolerationOperator.Equal -> ResourceDeploymentTolerationOperator.Equal
            | ComputeTolerationOperator.Exists -> ResourceDeploymentTolerationOperator.Exists
            | _ -> invalidArg (nameof value) "Unsupported toleration operator."

        let taintEffect value =
            match value with
            | ComputeTaintEffect.Any -> ResourceDeploymentTaintEffect.Any
            | ComputeTaintEffect.NoSchedule -> ResourceDeploymentTaintEffect.NoSchedule
            | ComputeTaintEffect.PreferNoSchedule -> ResourceDeploymentTaintEffect.PreferNoSchedule
            | ComputeTaintEffect.NoExecute -> ResourceDeploymentTaintEffect.NoExecute
            | _ -> invalidArg (nameof value) "Unsupported taint effect."

        ResourceDeploymentComputeConfiguration(
            profile.ComputeProfileId,
            profile.ComputeProfileRevision,
            profile.DeploymentTargetId,
            profile.DisplayName,
            ResourceDeploymentComputeResourceValues(
                profile.Requests.CpuMillis,
                profile.Requests.MemoryMi),
            ResourceDeploymentComputeResourceValues(
                profile.Limits.CpuMillis,
                profile.Limits.MemoryMi),
            architecture,
            profile.ExtendedResources
            |> Seq.map (fun resource ->
                ResourceDeploymentExtendedResource(resource.Name, resource.Quantity))
            |> ImmutableList.CreateRange,
            profile.NodeSelector
            |> Seq.map (fun selector ->
                ResourceDeploymentNodeSelector(selector.Key, selector.Value))
            |> ImmutableList.CreateRange,
            profile.RequiredNodeAffinity
            |> Seq.map affinityTerm
            |> ImmutableList.CreateRange,
            profile.PreferredNodeAffinity
            |> Seq.map (fun term ->
                ResourceDeploymentPreferredNodeAffinityTerm(
                    term.Weight,
                    affinityTerm term.Term))
            |> ImmutableList.CreateRange,
            profile.Tolerations
            |> Seq.map (fun toleration ->
                ResourceDeploymentToleration(
                    toleration.Key,
                    tolerationOperator toleration.Operator,
                    toleration.Value,
                    taintEffect toleration.Effect,
                    toleration.TolerationSeconds))
            |> ImmutableList.CreateRange,
            profile.RuntimeClassName)

    let storage (profile : ResolvedStorageProfile) =
        ResourceDeploymentStorageConfiguration(
            profile.ProfileId,
            profile.ProfileRevision,
            profile.StorageClassName)

    let private kubernetesAffinityOperator value =
        match value with
        | ResourceDeploymentNodeAffinityOperator.In -> KubernetesNodeAffinityOperator.In
        | ResourceDeploymentNodeAffinityOperator.NotIn -> KubernetesNodeAffinityOperator.NotIn
        | ResourceDeploymentNodeAffinityOperator.Exists -> KubernetesNodeAffinityOperator.Exists
        | ResourceDeploymentNodeAffinityOperator.DoesNotExist -> KubernetesNodeAffinityOperator.DoesNotExist
        | ResourceDeploymentNodeAffinityOperator.GreaterThan -> KubernetesNodeAffinityOperator.GreaterThan
        | ResourceDeploymentNodeAffinityOperator.LessThan -> KubernetesNodeAffinityOperator.LessThan
        | _ -> invalidArg (nameof value) "Unsupported node-affinity operator."

    let private kubernetesAffinityTerm (term : ResourceDeploymentNodeAffinityTerm) =
        KubernetesNodeAffinityTerm(
            term.Expressions
            |> Seq.map (fun expression ->
                KubernetesNodeAffinityExpression(
                    expression.Key,
                    kubernetesAffinityOperator expression.Operator,
                    expression.Values))
            |> ResizeArray)

    let kubernetesCompute (compute : ResourceDeploymentComputeConfiguration) =
        let architecture =
            match compute.Architecture with
            | ResourceDeploymentComputeArchitecture.Any -> KubernetesComputeArchitecture.Any
            | ResourceDeploymentComputeArchitecture.Amd64 -> KubernetesComputeArchitecture.Amd64
            | ResourceDeploymentComputeArchitecture.Arm64 -> KubernetesComputeArchitecture.Arm64
            | _ -> invalidArg (nameof compute) "Unsupported compute architecture."

        let tolerationOperator value =
            match value with
            | ResourceDeploymentTolerationOperator.Equal -> KubernetesTolerationOperator.Equal
            | ResourceDeploymentTolerationOperator.Exists -> KubernetesTolerationOperator.Exists
            | _ -> invalidArg (nameof value) "Unsupported toleration operator."

        let taintEffect value =
            match value with
            | ResourceDeploymentTaintEffect.Any -> KubernetesTaintEffect.Any
            | ResourceDeploymentTaintEffect.NoSchedule -> KubernetesTaintEffect.NoSchedule
            | ResourceDeploymentTaintEffect.PreferNoSchedule -> KubernetesTaintEffect.PreferNoSchedule
            | ResourceDeploymentTaintEffect.NoExecute -> KubernetesTaintEffect.NoExecute
            | _ -> invalidArg (nameof value) "Unsupported taint effect."

        KubernetesComputeConfiguration(
            KubernetesComputeResourceValues(
                compute.Requests.CpuMillis,
                compute.Requests.MemoryMi),
            KubernetesComputeResourceValues(
                compute.Limits.CpuMillis,
                compute.Limits.MemoryMi),
            architecture,
            compute.ExtendedResources
            |> Seq.map (fun resource ->
                KubernetesExtendedResource(resource.Name, resource.Quantity))
            |> ResizeArray,
            compute.NodeSelector
            |> Seq.map (fun selector ->
                KubernetesNodeSelector(selector.Key, selector.Value))
            |> ResizeArray,
            compute.RequiredNodeAffinity
            |> Seq.map kubernetesAffinityTerm
            |> ResizeArray,
            compute.PreferredNodeAffinity
            |> Seq.map (fun term ->
                KubernetesPreferredNodeAffinityTerm(
                    term.Weight,
                    kubernetesAffinityTerm term.Term))
            |> ResizeArray,
            compute.Tolerations
            |> Seq.map (fun toleration ->
                KubernetesToleration(
                    toleration.Key,
                    tolerationOperator toleration.Operator,
                    toleration.Value,
                    taintEffect toleration.Effect,
                    toleration.TolerationSeconds))
            |> ResizeArray,
            compute.RuntimeClassName)
