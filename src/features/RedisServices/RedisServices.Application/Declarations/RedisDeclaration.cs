using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application.Common;

namespace Pole.Features.RedisServices.Application.Declarations;

public sealed record RedisPinnedProfile(
    string Name,
    long Revision
);

public sealed record RedisDeclarationPlacement(
    string DeploymentTarget,
    RedisPinnedProfile ComputeProfile,
    string? IsolationScope
);

public sealed record RedisDeclarationStorage(
    int SizeGb,
    RedisPinnedProfile Profile
);

public sealed record RedisDeclaration
{
    public required string ResourceGroup { get; init; }
    public required RedisDeclarationPlacement Placement { get; init; }
    public required RedisDeclarationStorage Storage { get; init; }
    public string[] Tags { get; init; } = [];
}

public enum RedisDeclarationError
{
    AcceptedSnapshotRequired,
    ResourceDeleting,
    ResourceGroupUnavailable,
    IsolationScopeUnavailable,
    DeploymentTargetUnavailable,
    ComputeProfileUnavailable,
    StorageProfileUnavailable,
    PlacementUnavailable,
    ResourceRegistrationUnavailable,
    QuotaUnavailable,
    ExplicitAdoptionRequired,
    UnexpectedOperation,
}

public sealed record RedisDeclarationRejected(
    RedisDeclarationError Reason
) : IDeclarationFailure;

internal static class RedisDeclarations
{
    public const string Kind = "RedisService";
    public static ResourceTypeId ResourceType => RedisResourceDeployment.ResourceType;

    public static string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        RedisDeclarationRejected rejected => rejected.Reason switch
        {
            RedisDeclarationError.AcceptedSnapshotRequired => nameof(RedisDeclarationError.AcceptedSnapshotRequired),
            RedisDeclarationError.ResourceDeleting => nameof(RedisDeclarationError.ResourceDeleting),
            RedisDeclarationError.ResourceGroupUnavailable => nameof(RedisDeclarationError.ResourceGroupUnavailable),
            RedisDeclarationError.IsolationScopeUnavailable => nameof(RedisDeclarationError.IsolationScopeUnavailable),
            RedisDeclarationError.DeploymentTargetUnavailable => nameof(RedisDeclarationError
                .DeploymentTargetUnavailable),
            RedisDeclarationError.ComputeProfileUnavailable => nameof(RedisDeclarationError.ComputeProfileUnavailable),
            RedisDeclarationError.StorageProfileUnavailable => nameof(RedisDeclarationError.StorageProfileUnavailable),
            RedisDeclarationError.PlacementUnavailable => nameof(RedisDeclarationError.PlacementUnavailable),
            RedisDeclarationError.ResourceRegistrationUnavailable => nameof(RedisDeclarationError
                .ResourceRegistrationUnavailable),
            RedisDeclarationError.QuotaUnavailable => nameof(RedisDeclarationError.QuotaUnavailable),
            RedisDeclarationError.ExplicitAdoptionRequired => nameof(RedisDeclarationError.ExplicitAdoptionRequired),
            RedisDeclarationError.UnexpectedOperation => nameof(RedisDeclarationError.UnexpectedOperation),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure)),
    };

    public static JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<RedisDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup), DeclarationFieldPaths.ResourceGroup);
        DeclarationJson.Require(value.Placement is not null, DeclarationFieldPaths.Placement);
        DeclarationJson.Require(
            DeclarationNames.Valid(value.Placement.DeploymentTarget),
            DeclarationFieldPaths.PlacementDeploymentTarget);
        DeclarationJson.Require(
            value.Placement.IsolationScope is null || (DeclarationNames.Valid(value.Placement.IsolationScope) &&
                value.Placement.IsolationScope != "default"),
            DeclarationFieldPaths.PlacementIsolationScope);
        DeclarationJson.Require(
            ValidProfile(value.Placement.ComputeProfile),
            DeclarationFieldPaths.PlacementComputeProfile);
        DeclarationJson.Require(
            value.Storage is not null && value.Storage.SizeGb is >= 1 and <= 1000,
            DeclarationFieldPaths.StorageSizeGb);
        DeclarationJson.Require(ValidProfile(value.Storage.Profile), DeclarationFieldPaths.StorageProfile);
        DeclarationJson.Require(
            value.Tags is not null && value.Tags.All(tag => !string.IsNullOrWhiteSpace(tag)),
            DeclarationFieldPaths.Tags);
        return DeclarationJson.Write(
            value with
            {
                Tags = value.Tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            });
    }

    public static DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<RedisDeclaration>(spec);
        return new DeclarationFacts(
            new ResourceAddress("ResourceGroup", value.ResourceGroup),
            [],
            value.Placement.IsolationScope is { } scope
                ?
                [
                    new DeclaredReference(
                        new ResourceAddress("IsolationScope", scope),
                        DeclarationFieldPaths.PlacementIsolationScope),
                ]
                : [],
            [],
            []);
    }

    public static IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<RedisDeclaration>(spec);
        return
        [
            new DeclarationStep(
                DeclarationPhase.Prepare,
                [
                    new DeclarationDependency(
                        new ResourceAddress("ResourceGroup", value.ResourceGroup),
                        DeclarationPhase.Prepare),
                    .. value.Placement.IsolationScope is { } scope
                        ? new DeclarationDependency[]
                            { new(new ResourceAddress("IsolationScope", scope), DeclarationPhase.Prepare) }
                        : [],
                ]),
            new DeclarationStep(
                DeclarationPhase.Configure,
                [new DeclarationDependency(address, DeclarationPhase.Prepare)]),
        ];
    }

    private static bool ValidProfile(RedisPinnedProfile? profile) =>
        profile is not null && DeclarationNames.Valid(profile.Name) && profile.Revision > 0;

    internal static DeclarationReadBlocked Blocked(RedisDeclarationError reason) =>
        new(new RedisDeclarationRejected(reason));
}
