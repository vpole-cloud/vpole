using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.ComputeProfiles.Application.Persistence;
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
                return Task.FromResult<Result<
                    ComputeProfile,
                    PersistCreatedComputeProfileError>>(new Ok<ComputeProfile>(profile));
            });
        services.AddScoped<IDeploymentTargetLookup, DefaultTargetLookup>();
        await using var serviceProvider = services.BuildServiceProvider();
        var bootstrapper = new ComputeProfileBootstrapper(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(),
            NullLogger<ComputeProfileBootstrapper>.Instance);

        await bootstrapper.BootstrapAsync();
        await bootstrapper.BootstrapAsync();

        var profile = Assert.IsType<ComputeProfile>(durableDefault);
        Assert.Equal(1, persistCount);
        Assert.True(profile.IsDefault);
        Assert.True(profile.IsEnabled);
        Assert.Equal(ComputeArchitecture.Any, profile.Architecture);
        Assert.Equal(250, profile.Resources.Requests.CpuMillis);
        Assert.Equal(256, profile.Resources.Requests.MemoryMi);
        Assert.Equal(1000, profile.Resources.Limits.CpuMillis);
        Assert.Equal(1024, profile.Resources.Limits.MemoryMi);
        Assert.Empty(profile.ExtendedResources);
        Assert.Equal(
            ["pole/postgresql", "pole/redis", "pole/web-service"],
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

    private sealed class DefaultTargetLookup : IDeploymentTargetLookup
    {
        public Task<Result<DeploymentTargetLookupDto>> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<DeploymentTargetLookupDto>>(CreateTarget(deploymentTargetId));

        public Task<Result<DeploymentTargetLookupDto>> GetDefaultAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<DeploymentTargetLookupDto>>(CreateTarget(DeploymentTargetId));

        public Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
            string? deploymentTargetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<DeploymentTargetLookupDto>>(
                CreateTarget(deploymentTargetId ?? DeploymentTargetId));

        public Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>>
            ListEligibleForNewWorkloadAsync(
                CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<IReadOnlyList<DeploymentTargetLookupDto>>>(
                new[] { CreateTarget(DeploymentTargetId) });

        private static DeploymentTargetLookupDto CreateTarget(string id) =>
            new(
                id,
                "Default",
                "local-cluster",
                "in-cluster",
                1,
                new DeploymentTargetWorkloadConfigurationLookupDto(1, null),
                1,
                Enabled: true,
                AcceptsWorkloads: true,
                DeletionRequested: false,
                HealthStatus: "Healthy");
    }
}
