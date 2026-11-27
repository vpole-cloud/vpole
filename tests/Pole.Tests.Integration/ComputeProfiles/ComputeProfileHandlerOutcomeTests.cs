// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Validation;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileHandlerOutcomeTests
{
    private const string ProfileId = "507f1f77bcf86cd799439096";
    private const string MissingProfileId = "507f1f77bcf86cd799439097";
    private const string TargetId = "507f1f77bcf86cd799439071";

    [Fact]
    public async Task Get_page_wraps_the_read_model_in_its_request_outcome()
    {
        LoadComputeProfilePage loadPage = (query, cancellationToken) => Task.FromResult(new ComputeProfilePage([], 0));
        var handler = new GetComputeProfilesHandler(loadPage);

        var outcome = await handler.HandleAsync(new GetComputeProfilesRequest());

        var page = outcome switch
        {
            ComputeProfilePageDto value => value,
            _ => throw new XunitException("Expected the compute-profile page outcome."),
        };
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Get_available_preserves_the_active_tenant_in_its_capability_query()
    {
        ComputeProfileAvailabilityQuery? received = null;
        LoadAvailableComputeProfiles loadAvailable = (query, cancellationToken) =>
        {
            received = query;
            return Task.FromResult<IReadOnlyList<ComputeProfile>>([]);
        };
        var handler = new GetAvailableComputeProfilesHandler(loadAvailable);

        var outcome = await handler.HandleAsync(
            new GetAvailableComputeProfilesRequest(TargetId, "pole/web-service", "507f1f77bcf86cd799439098"));

        var profiles = outcome switch
        {
            IReadOnlyCollection<AvailableComputeProfileDto> value => value,
            _ => throw new XunitException("Expected the available compute-profiles outcome."),
        };
        Assert.Empty(profiles);
        Assert.NotNull(received);
        Assert.Equal("507f1f77bcf86cd799439098", received.TenantId);
        Assert.Equal(TargetId, received.DeploymentTargetId);
        Assert.Equal("pole/web-service", received.ResourceType.Value);
    }

    [Fact]
    public async Task Create_returns_target_reference_rejection_directly()
    {
        PersistCreatedComputeProfile persist = (profile, cancellationToken) =>
            Task.FromResult<PersistCreatedComputeProfileOutcome>(
                new DeploymentTargetReferenceChangeRejected(
                    new DeploymentTargetReferenceOwner("compute-profile", profile.Id),
                    [TargetId]));
        var handler = new CreateComputeProfileHandler(
            ValidTenantAccess,
            ValidTargetMappings,
            persist,
            TimeProvider.System);

        var outcome = await handler.HandleAsync(new CreateComputeProfileRequest(ValidCreateBody()));

        var rejected = Assert.IsType<DeploymentTargetReferenceChangeRejected>(outcome.Value);
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
            (
                _, _,
                _) => throw new XunitException("Persistence must not be called."),
            TimeProvider.System);

        var outcome = await handler.HandleAsync(
            new UpdateComputeProfileRequest(MissingProfileId, ValidUpdateBody(revision: 1)));

        var notFound = Assert.IsType<ComputeProfileNotFound>(outcome.Value);
        Assert.Equal(MissingProfileId, notFound.ComputeProfileId);
    }

    [Fact]
    public async Task Update_returns_default_demotion_rejection_directly()
    {
        var current = ComputeProfileTestData.CreateProfile(TargetId, isDefault: true, id: ProfileId);
        LoadComputeProfileById load = (computeProfileId, cancellationToken) =>
            Task.FromResult<ComputeProfile?>(current);
        PersistUpdatedComputeProfile persist = (
                existing, replacement,
                cancellationToken) =>
            Task.FromResult<PersistUpdatedComputeProfileOutcome>(new DefaultComputeProfileCannotBeDemoted(existing.Id));
        var handler = new UpdateComputeProfileHandler(
            load,
            ValidTenantAccess,
            ValidTargetMappings,
            persist,
            TimeProvider.System);

        var outcome = await handler.HandleAsync(
            new UpdateComputeProfileRequest(current.Id, ValidUpdateBody(current.Revision)));

        var rejected = Assert.IsType<DefaultComputeProfileCannotBeDemoted>(outcome.Value);
        Assert.Equal(current.Id, rejected.ComputeProfileId);
    }

    private static Task<ValidateComputeProfileTenantAccessOutcome> ValidTenantAccess(
        ComputeProfileTenantAccess tenantAccess,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidateComputeProfileTenantAccessOutcome>(new ComputeProfileTenantAccessValid());

    private static Task<ValidateComputeProfileTargetMappingsOutcome> ValidTargetMappings(
        ComputeProfile profile,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ValidateComputeProfileTargetMappingsOutcome>(new ComputeProfileTargetMappingsValid());

    private static CreateComputeProfileRequestBody ValidCreateBody() => new(
        "test-profile",
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

    private static UpdateComputeProfileRequestBody ValidUpdateBody(long revision) => new(
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
