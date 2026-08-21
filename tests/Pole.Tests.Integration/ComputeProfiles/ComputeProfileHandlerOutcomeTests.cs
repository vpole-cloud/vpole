using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileHandlerOutcomeTests
{
    [Fact]
    public async Task Create_wraps_target_reference_rejection_in_its_request_outcome()
    {
        PersistCreatedComputeProfile persist = (profile, cancellationToken) =>
            Task.FromResult<Result<ComputeProfile, PersistCreatedComputeProfileError>>(
                new Err<PersistCreatedComputeProfileError>(
                    new PersistCreatedComputeProfileError(
                        new DeploymentTargetReferenceChangeRejected(
                            new DeploymentTargetReferenceOwner("compute-profile", profile.Id),
                            [TargetId]))));
        var handler = new CreateComputeProfileHandler(
            ValidTenantAccess,
            ValidTargetMappings,
            persist,
            TimeProvider.System);

        var outcome = await handler.HandleAsync(new CreateComputeProfileRequest(ValidCreateBody()));

        var error = outcome switch
        {
            CreateComputeProfileError failure => failure.Value,
            ComputeProfileDto => throw new Xunit.Sdk.XunitException(
                "Expected create to return an error outcome.")
        };
        var persistenceError = Assert.IsType<PersistCreatedComputeProfileError>(error);
        var rejected = Assert.IsType<DeploymentTargetReferenceChangeRejected>(
            persistenceError.Value);
        Assert.Equal([TargetId], rejected.DeploymentTargetIds);
    }

    [Fact]
    public async Task Update_missing_profile_returns_its_request_specific_not_found_leaf()
    {
        LoadComputeProfileById missing = (computeProfileId, cancellationToken) =>
            Task.FromResult<ComputeProfile?>(null);
        var handler = new UpdateComputeProfileHandler(
            missing,
            ValidTenantAccess,
            ValidTargetMappings,
            (_, _, _) => throw new Xunit.Sdk.XunitException("Persistence must not be called."),
            TimeProvider.System);

        var outcome = await handler.HandleAsync(new UpdateComputeProfileRequest(
            MissingProfileId,
            ValidUpdateBody(revision: 1)));

        var notFound = outcome switch
        {
            UpdateComputeProfileError failure =>
                Assert.IsType<ComputeProfileNotFound>(failure.Value),
            ComputeProfileDto => throw new Xunit.Sdk.XunitException(
                "Expected update to return a not-found outcome.")
        };
        Assert.Equal(MissingProfileId, notFound.ComputeProfileId);
    }

    [Fact]
    public async Task Update_wraps_default_demotion_rejection_in_its_request_outcome()
    {
        var current = ComputeProfileTestData.CreateProfile(
            TargetId,
            isDefault: true,
            id: ProfileId);
        LoadComputeProfileById load = (computeProfileId, cancellationToken) =>
            Task.FromResult<ComputeProfile?>(current);
        PersistUpdatedComputeProfile persist = (existing, replacement, cancellationToken) =>
            Task.FromResult<Result<ComputeProfile, PersistUpdatedComputeProfileError>>(
                new Err<PersistUpdatedComputeProfileError>(
                    new PersistUpdatedComputeProfileError(
                        new DefaultComputeProfileCannotBeDemoted(existing.Id))));
        var handler = new UpdateComputeProfileHandler(
            load,
            ValidTenantAccess,
            ValidTargetMappings,
            persist,
            TimeProvider.System);

        var outcome = await handler.HandleAsync(new UpdateComputeProfileRequest(
            current.Id,
            ValidUpdateBody(current.Revision)));

        var error = outcome switch
        {
            UpdateComputeProfileError failure => failure.Value,
            ComputeProfileDto => throw new Xunit.Sdk.XunitException(
                "Expected update to return an error outcome.")
        };
        var persistenceError = Assert.IsType<PersistUpdatedComputeProfileError>(error);
        var rejected = Assert.IsType<DefaultComputeProfileCannotBeDemoted>(
            persistenceError.Value);
        Assert.Equal(current.Id, rejected.ComputeProfileId);
    }

    private const string ProfileId = "507f1f77bcf86cd799439096";
    private const string MissingProfileId = "507f1f77bcf86cd799439097";
    private const string TargetId = "507f1f77bcf86cd799439071";

    private static Task<Result<Unit, SelectedComputeProfileTenantsNotFound>>
        ValidTenantAccess(
            ComputeProfileTenantAccess tenantAccess,
            CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<Unit, SelectedComputeProfileTenantsNotFound>>(
            new Ok<Unit>(Unit.Value));

    private static Task<Result<Unit, ComputeProfileTargetMappingsInvalid>>
        ValidTargetMappings(
            ComputeProfile profile,
            CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<Unit, ComputeProfileTargetMappingsInvalid>>(
            new Ok<Unit>(Unit.Value));

    private static CreateComputeProfileRequestBody ValidCreateBody() =>
        new(
            "General purpose",
            "Compute profile",
            new ComputeResourceRequirementsRequestBody(
                new ComputeResourceValuesRequestBody(250, 256),
                new ComputeResourceValuesRequestBody(1000, 1024)),
            "Any",
            [],
            ["pole/web-service"],
            new ComputeProfileTenantAccessRequestBody("all-tenants", []),
            [new ComputeProfileTargetMappingRequestBody(TargetId, [], [], [], [], null)],
            IsEnabled: true,
            IsDefault: false);

    private static UpdateComputeProfileRequestBody ValidUpdateBody(long revision) =>
        new(
            "General purpose updated",
            "Compute profile",
            new ComputeResourceRequirementsRequestBody(
                new ComputeResourceValuesRequestBody(250, 256),
                new ComputeResourceValuesRequestBody(1000, 1024)),
            "Any",
            [],
            ["pole/web-service"],
            new ComputeProfileTenantAccessRequestBody("all-tenants", []),
            [new ComputeProfileTargetMappingRequestBody(TargetId, [], [], [], [], null)],
            IsEnabled: true,
            IsDefault: false,
            Revision: revision);
}
