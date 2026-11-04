using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;
using Pole.Features.ComputeProfiles.Application.Persistence.Reads;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

public sealed class ComputeProfileBootstrapperTests
{
    [Fact]
    public async Task Bootstrap_creates_the_built_in_default_once_with_the_agreed_values()
    {
        ComputeProfile? durableDefault = null;
        var persistCount = 0;
        var services = new ServiceCollection();
        services.AddScoped<LoadDefaultComputeProfile>(_ =>
            cancellationToken => Task.FromResult(durableDefault));
        services.AddScoped<PersistCreatedComputeProfile>(_ =>
            (profile, cancellationToken) =>
            {
                persistCount++;
                durableDefault = profile;
                return Task.FromResult<PersistCreatedComputeProfileOutcome>(profile);
            });
        services.AddScoped<LookupDefaultDeploymentTarget>(_ =>
            cancellationToken => Task.FromResult<LookupDefaultDeploymentTargetOutcome>(
                new LookupDefaultDeploymentTargetOutcome(CreateTarget(DeploymentTargetId))));
        await using var serviceProvider = services.BuildServiceProvider();
        var bootstrapper = new ComputeProfileBootstrapper(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(),
            NullLogger<ComputeProfileBootstrapper>.Instance);

        await bootstrapper.BootstrapAsync();
        await bootstrapper.BootstrapAsync();

        var profile = Assert.IsType<ComputeProfile>(durableDefault);
        Assert.Equal(1, persistCount);
        Assert.Equal("default", profile.TechnicalName);
        Assert.Equal(1, profile.Revision);
        Assert.True(profile.IsDefault);
        Assert.True(profile.IsEnabled);
        Assert.Equal(ComputeArchitecture.Any, profile.Architecture);
        Assert.Equal(250, profile.Resources.Requests.CpuMillis);
        Assert.Equal(256, profile.Resources.Requests.MemoryMi);
        Assert.Equal(1000, profile.Resources.Limits.CpuMillis);
        Assert.Equal(1024, profile.Resources.Limits.MemoryMi);
        Assert.Empty(profile.ExtendedResources);
        Assert.Equal(
            ["pole/function-app", "pole/postgresql", "pole/redis", "pole/web-service"],
            profile.AllowedResourceTypes.Order(StringComparer.Ordinal));
        Assert.Equal(
            DeploymentTargetId,
            profile.TargetMappings.Single().DeploymentTargetId);
    }

    private const string DeploymentTargetId = "507f1f77bcf86cd799439081";

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    }

    private static DeploymentTargetLookupDto CreateTarget(string id) =>
        new(
            id,
            "Default",
            "local-cluster",
            "in-cluster",
            1,
            new DeploymentTargetWorkloadConfigurationLookupDto(
                1, null, new DeploymentTargetNetworkSourcesDto(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
            1,
            Enabled: true,
            AcceptsWorkloads: true,
            DeletionRequested: false,
            HealthStatus: "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown);
}
