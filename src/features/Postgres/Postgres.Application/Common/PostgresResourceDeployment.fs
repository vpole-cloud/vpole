namespace Pole.Features.Postgres.Application.Common

open System.Collections.Immutable
open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.Core.Kubernetes
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.StorageProfiles.Contracts
open Pole.Workloads

[<AbstractClass; Sealed>]
type PostgresResourceDeployment =
    static member ResourceType = ResourceTypeId.Create("pole/postgresql")

[<RequireQualifiedAccess>]
module PostgresResourceDeploymentMappings =
    let private affinityTerm (term : ComputeNodeAffinityTermValue) =
        term.Expressions
        |> Seq.map (fun expression ->
            ResourceDeploymentNodeAffinityExpression(
                expression.Key,
                expression.Operator,
                expression.Values))
        |> ImmutableList.CreateRange
        |> ResourceDeploymentNodeAffinityTerm

    let compute (profile : ResolvedComputeProfile) =
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
            profile.Architecture,
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
                    toleration.Operator,
                    toleration.Value,
                    toleration.Effect,
                    toleration.TolerationSeconds))
            |> ImmutableList.CreateRange,
            profile.RuntimeClassName)

    let storage (profile : ResolvedStorageProfile) =
        ResourceDeploymentStorageConfiguration(
            profile.ProfileId,
            profile.ProfileRevision,
            profile.StorageClassName)

    let private kubernetesAffinityTerm (term : ResourceDeploymentNodeAffinityTerm) =
        KubernetesNodeAffinityTerm(
            term.Expressions
            |> Seq.map (fun expression ->
                KubernetesNodeAffinityExpression(
                    expression.Key,
                    expression.Operator,
                    expression.Values))
            |> ResizeArray)

    let kubernetesCompute (compute : ResourceDeploymentComputeConfiguration) =
        KubernetesComputeConfiguration(
            KubernetesComputeResourceValues(
                compute.Requests.CpuMillis,
                compute.Requests.MemoryMi),
            KubernetesComputeResourceValues(
                compute.Limits.CpuMillis,
                compute.Limits.MemoryMi),
            compute.Architecture,
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
                    toleration.Operator,
                    toleration.Value,
                    toleration.Effect,
                    toleration.TolerationSeconds))
            |> ResizeArray,
            compute.RuntimeClassName)
