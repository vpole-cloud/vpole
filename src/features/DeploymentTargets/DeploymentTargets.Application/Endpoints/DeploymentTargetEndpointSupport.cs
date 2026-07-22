using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

internal static class DeploymentTargetEndpointSupport
{
    public static async Task<Result<DeploymentTarget>> GetTargetAsync(
        IDeploymentTargetDbContext dbContext,
        string id,
        CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(id, out _))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Id"] = ["Deployment target ID must be a MongoDB ObjectId."]
            });
        }

        var target = await dbContext.DeploymentTargets
            .Find(item => item.Id == id)
            .FirstOrDefaultAsync(cancellationToken);
        return target is null
            ? Failures.NotFound("Deployment target not found.", id)
            : target;
    }

    public static async Task<string?> GetDefaultIdAsync(
        IDeploymentTargetDbContext dbContext,
        CancellationToken cancellationToken) =>
        await dbContext.DeploymentTargetSettings
            .Find(settings => settings.Id == DeploymentTargetSettings.SingletonId)
            .Project(settings => settings.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);

    public static FilterDefinition<DeploymentTarget> RevisionFilter(DeploymentTarget target) =>
        Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Eq(item => item.Id, target.Id),
            Builders<DeploymentTarget>.Filter.Eq(item => item.Revision, target.Revision));

    public static DateTime CheckedAtUtc(TimeProvider timeProvider, DateTime? lastHealthyAtUtc)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return lastHealthyAtUtc is { } lastHealthy && lastHealthy > now
            ? lastHealthy
            : now;
    }

    public static Result<DeploymentTargetConnectionInput> ToConnectionInput(
        DeploymentTargetConnectionRequestBody? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Kind))
            return BodyValidationFailure("Connection kind is required.");
        if (!Enum.TryParse<DeploymentTargetConnectionKind>(body.Kind, true, out var kind))
            return BodyValidationFailure("Connection kind is invalid.");

        return kind switch
        {
            DeploymentTargetConnectionKind.InCluster when HasRemoteFields(body) =>
                BodyValidationFailure("InCluster connections cannot contain remote credentials."),
            DeploymentTargetConnectionKind.InCluster => new InClusterConnectionInput(),
            DeploymentTargetConnectionKind.Kubeconfig when
                string.IsNullOrWhiteSpace(body.Kubeconfig) ||
                HasServiceAccountFields(body) =>
                BodyValidationFailure("Kubeconfig connection fields are invalid."),
            DeploymentTargetConnectionKind.Kubeconfig =>
                new KubeconfigConnectionInput(body.Kubeconfig!, body.ContextName),
            DeploymentTargetConnectionKind.ServiceAccount when
                string.IsNullOrWhiteSpace(body.ServerUrl) ||
                string.IsNullOrWhiteSpace(body.CertificateAuthorityData) ||
                string.IsNullOrWhiteSpace(body.Token) ||
                body.Kubeconfig is not null || body.ContextName is not null =>
                BodyValidationFailure("ServiceAccount connection fields are invalid."),
            DeploymentTargetConnectionKind.ServiceAccount =>
                new ServiceAccountConnectionInput(
                    body.ServerUrl!,
                    body.CertificateAuthorityData!,
                    body.Token!),
            _ => BodyValidationFailure("Connection kind is invalid.")
        };
    }

    public static ValidationFailure BodyValidationFailure(string message) =>
        Failures.ValidationFailure(new Dictionary<string, string[]>
        {
            ["Body"] = [message]
        });

    public static ValidationFailure ConnectionValidationFailure(string safeMessage) =>
        Failures.ValidationFailure(new Dictionary<string, string[]>
        {
            ["Body.Connection"] = [safeMessage]
        });

    private static bool HasRemoteFields(DeploymentTargetConnectionRequestBody body) =>
        body.Kubeconfig is not null ||
        body.ContextName is not null ||
        body.ServerUrl is not null ||
        body.CertificateAuthorityData is not null ||
        body.Token is not null;

    private static bool HasServiceAccountFields(DeploymentTargetConnectionRequestBody body) =>
        body.ServerUrl is not null ||
        body.CertificateAuthorityData is not null ||
        body.Token is not null;
}
