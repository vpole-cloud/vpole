using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Bootstrap;

public interface IComputeProfileBootstrapper
{
    Task BootstrapAsync(CancellationToken cancellationToken = default);
}

public sealed class ComputeProfileBootstrapper(
    IServiceScopeFactory serviceScopeFactory,
    TimeProvider timeProvider,
    ILogger<ComputeProfileBootstrapper> logger)
    : IComputeProfileBootstrapper
{
    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var loadDefault = scope.ServiceProvider
            .GetRequiredService<LoadDefaultComputeProfile>();
        if (await loadDefault(cancellationToken) is not null)
            return;

        var deploymentTargetLookup = scope.ServiceProvider
            .GetRequiredService<IDeploymentTargetLookup>();
        var targetResult = await deploymentTargetLookup.ResolveForNewWorkloadAsync(
            deploymentTargetId: null,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
        {
            throw new InvalidOperationException(
                $"The default compute profile cannot be bootstrapped: {targetFailure.Message}");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var profile = ComputeProfileFactory.Create(
            "Default",
            "General-purpose profile for local and small installations.",
            ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(250, 256),
                ComputeProfileResourceQuantity.Create(1000, 1024)),
            ComputeArchitecture.Any,
            extendedResources: [],
            allowedResourceTypes:
            [
                ResourceTypeId.Create("pole/web-service"),
                ResourceTypeId.Create("pole/redis"),
                ResourceTypeId.Create("pole/postgresql")
            ],
            AllTenantsComputeProfileAccess.Create(),
            [ComputeProfileTargetMapping.Create(target.Id)],
            isEnabled: true,
            isDefault: true,
            new ComputeProfileFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now));

        var persistCreated = scope.ServiceProvider
            .GetRequiredService<PersistCreatedComputeProfile>();
        try
        {
            var persisted = await persistCreated(profile, cancellationToken);
            if (persisted is ISuccess)
            {
                logger.LogInformation(
                    "Bootstrapped default compute profile {ComputeProfileId} for deployment target {DeploymentTargetId}.",
                    profile.Id,
                    target.Id);
                return;
            }
        }
        catch (MongoException exception) when (IsDuplicateKey(exception))
        {
            // Another replica may have committed the unique default first. The durable state
            // below decides whether this was the expected bootstrap race.
        }

        if (await loadDefault(cancellationToken) is not null)
            return;

        throw new InvalidOperationException(
            "The default compute profile could not be bootstrapped.");
    }

    private static bool IsDuplicateKey(MongoException exception) =>
        exception switch
        {
            MongoWriteException
            {
                WriteError.Category: ServerErrorCategory.DuplicateKey
            } => true,
            MongoCommandException { Code: 11000 or 11001 or 12582 } => true,
            _ => false
        };
}
