using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.RedisServices.Application.Declarations;

internal static class RedisPlanning
{
    internal static JsonElement ComputeProjection(ResourceDeploymentComputeConfiguration compute) =>
        DeclarationJson.Write(new
        {
            compute.Requests,
            compute.Limits,
            compute.Architecture,
            compute.RuntimeClassName,
            extendedResources = compute.ExtendedResources.OrderBy(item => item.Name, StringComparer.Ordinal),
            nodeSelector = compute.NodeSelector.OrderBy(item => item.Key, StringComparer.Ordinal),
            requiredNodeAffinity = compute.RequiredNodeAffinity.Select(NormalizeTerm)
                .OrderBy(item => DeclarationJson.Write(item).GetRawText(), StringComparer.Ordinal),
            preferredNodeAffinity = compute.PreferredNodeAffinity.Select(item =>
                    item with { Term = NormalizeTerm(item.Term) })
                .OrderBy(item => DeclarationJson.Write(item).GetRawText(), StringComparer.Ordinal),
            tolerations = compute.Tolerations.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ThenBy(item => item.Operator).ThenBy(item => item.Value, StringComparer.Ordinal)
                .ThenBy(item => item.Effect).ThenBy(item => item.TolerationSeconds)
        });

    internal static ResourceDeploymentNodeAffinityTerm NormalizeTerm(ResourceDeploymentNodeAffinityTerm term) =>
        term with
        {
            Expressions = term.Expressions.Select(item => item with
            { Values = item.Values.Order(StringComparer.Ordinal).ToImmutableList() })
                .OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Operator).ToImmutableList()
        };

    internal static DeclarationDocumentVersion[] DependencyVersions(RedisPlanningDependencies dependencies) =>
    [
        new(
            ResourceGroupVersionKeys.Revision(dependencies.Scope.ResourceGroupId),
            dependencies.Scope.ResourceGroupRevision),
        new(
            ResourceGroupVersionKeys.IsolationScopeRevision(dependencies.Scope.IsolationScopeId),
            dependencies.Scope.ScopeRevision),
        new(
            DeploymentTargetVersionKeys.Revision(dependencies.Target.Target.Id),
            dependencies.Target.Target.Revision),
        new(DeploymentTargetVersionKeys.WorkloadConfiguration(dependencies.Target.Target.Id),
            dependencies.Target.Target.WorkloadConfiguration.Revision),
        new(ComputeProfileVersionKeys.Policy(dependencies.Compute.Compute.ComputeProfileId),
            dependencies.Compute.PolicyRevision),
        new(
            StorageProfileVersionKeys.Policy(dependencies.Storage.Storage.ProfileId),
            dependencies.Storage.PolicyRevision),
        .. dependencies.Placements?.Items.Select(item =>
            new DeclarationDocumentVersion(
                ResourceDeploymentVersionKeys.Revision(item.DeploymentId), item.Revision)) ?? []
    ];

    internal static DeclarationProjection Project(
        DeclarationPhase phase,
        RedisManagedIdentity identity,
        ResourceDeploymentComputeConfiguration compute,
        IEnumerable<string> tags) =>
        new(DeclarationJson.Write(identity), phase == DeclarationPhase.Prepare
            ? DeclarationJson.Write(new { })
            : DeclarationJson.Write(new
            {
                tags = tags.Order(StringComparer.Ordinal),
                computeProfileId = compute.ComputeProfileId,
                computeProfileRevision = compute.ComputeProfileRevision,
                compute = ComputeProjection(compute)
            }));

    internal static ResourceDeploymentStorageConfiguration StorageConfiguration(ResolvedStorageProfile storage) =>
        new(storage.ProfileId, storage.ProfileRevision, storage.StorageClassName);
}

internal sealed record RedisManagedIdentity(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    int StorageSizeGb,
    string StorageProfileId,
    ResourceDeploymentStorageConfiguration Storage);

internal sealed record RedisPlanningDependencies(
    Redis? Current,
    IsolationScopePlanningSnapshot Scope,
    DeploymentTargetPlanningSnapshot Target,
    ComputeProfilePlanningSnapshot Compute,
    StorageProfilePlanningSnapshot Storage,
    ResourceDeploymentPlanningSnapshot? Placements);

internal union ReadRedisPlanningOutcome(
    RedisPlanningDependencies,
    DeclarationReadBlocked);
