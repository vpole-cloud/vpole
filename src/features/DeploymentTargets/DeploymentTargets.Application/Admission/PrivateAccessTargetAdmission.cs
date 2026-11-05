using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Admission;

public static class PrivateAccessTargetAdmissionRegistration
{
    public static IServiceCollection AddPrivateAccessTargetAdmission(
        this IServiceCollection services)
    {
        services.TryAddScoped<MongoFenceDeploymentTargetPrivateAccess>();
        services.TryAddScoped<FenceDeploymentTargetPrivateAccess>(provider => provider
            .GetRequiredService<MongoFenceDeploymentTargetPrivateAccess>()
            .InvokeAsync);
        return services;
    }
}

internal sealed class MongoFenceDeploymentTargetPrivateAccess(
    IMongoCollection<DeploymentTarget> targets)
{
    public async Task<FenceDeploymentTargetPrivateAccessOutcome> InvokeAsync(
        string deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Private access target admission requires a Mongo transaction.");
        var target = await targets.FindOneAndUpdateAsync(
            session,
            DeploymentTargetFilters.ById(deploymentTargetId),
            Builders<DeploymentTarget>.Update.Inc(item => item.ReferenceFence, 1),
            new FindOneAndUpdateOptions<DeploymentTarget> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (target is null || !target.Enabled || target.DeletionRequestedAtUtc is not null ||
            !target.Capabilities.NetworkPolicyCapabilities.CanManage ||
            target.WorkloadConfiguration.NetworkPolicy.EffectiveEnforcement(target) !=
                NetworkPolicyEnforcement.Supported)
            return new PrivateAccessDeploymentTargetUnavailable();
        return new PrivateAccessDeploymentTargetReady(target.Id, target.Revision);
    }
}
