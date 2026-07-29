using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Bootstrap;

public interface IDeploymentTargetBootstrapper
{
    Task BootstrapAsync(CancellationToken cancellationToken = default);
}

public sealed class DeploymentTargetBootstrapper(
    IDeploymentTargetDbContext dbContext,
    IDeploymentTargetConnectionPreparer connectionPreparer,
    IDeploymentTargetProbe probe,
    DeploymentTargetOptions options,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<DeploymentTargetBootstrapper> logger) : IDeploymentTargetBootstrapper
{
    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        var current = await LoadCurrentAsync(cancellationToken);
        if (current.Settings is not null)
        {
            if (current.DefaultTargetExists)
                return;

            throw new InvalidOperationException(
                "Deployment-target settings reference a target that does not exist.");
        }

        if (current.FirstTarget is not null)
        {
            var ensured = await EnsureDefaultSettingsAsync(current.FirstTarget.Id, cancellationToken);
            if (ensured is ISuccess)
                return;

            current = await LoadCurrentAsync(cancellationToken);
            if (current.Settings is not null && current.DefaultTargetExists)
                return;

            throw new InvalidOperationException(
                "The existing deployment target could not be bound as the default target.");
        }

        if (!options.Bootstrap.Enabled)
        {
            logger.LogWarning(
                "Deployment-target bootstrap is disabled and no default deployment target is configured.");
            return;
        }

        var targetId = ObjectId.GenerateNewId().ToString();
        var input = await CreateBootstrapInputAsync(cancellationToken);
        var preparedResult = await connectionPreparer.PrepareAsync(
            targetId,
            connectionVersion: 1,
            input,
            cancellationToken);
        if (!preparedResult.TryGetValue(out var prepared, out var prepareFailure))
        {
            throw new InvalidOperationException(
                $"The local deployment-target connection could not be prepared: {prepareFailure.Message}");
        }

        using (prepared)
        {
            var observation = await probe.ProbeAsync(
                prepared.Candidate,
                prepared.Connection.Version,
                cancellationToken: cancellationToken);
            if (!observation.IsUsable || observation.ClusterIdentity is null)
            {
                throw new InvalidOperationException(
                    $"The local deployment target could not be validated ({observation.Health.FailureCode}).");
            }

            var createdAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var target = DeploymentTargetFactory.Create(
                options.Bootstrap.DisplayName,
                observation.ClusterIdentity,
                prepared.Connection,
                observation.Health,
                observation.Capabilities,
                CreateWorkloadConfiguration(),
                enabled: true,
                acceptsWorkloads: true,
                new DeploymentTargetFactory.CreateOptions(targetId, createdAtUtc));

            var persisted = await PersistFirstTargetAsync(target, cancellationToken);
            if (persisted is ISuccess)
            {
                logger.LogInformation(
                    "Bootstrapped default deployment target {DeploymentTargetId} with connection kind {ConnectionKind}.",
                    target.Id,
                    target.Connection.Kind);
                return;
            }
        }

        // A second replica may have won the first-target transaction. Treat that exact durable
        // outcome as success; any other conflict means startup state is inconsistent.
        current = await LoadCurrentAsync(cancellationToken);
        if (current.Settings is not null && current.DefaultTargetExists)
            return;

        throw new InvalidOperationException("The default deployment target could not be bootstrapped.");
    }

    private async Task<DeploymentTargetConnectionInput> CreateBootstrapInputAsync(
        CancellationToken cancellationToken)
    {
        var mode = options.Bootstrap.Mode switch
        {
            DeploymentTargetBootstrapMode.Auto when environment.IsDevelopment() =>
                DeploymentTargetBootstrapMode.Kubeconfig,
            DeploymentTargetBootstrapMode.Auto => DeploymentTargetBootstrapMode.InCluster,
            DeploymentTargetBootstrapMode.InCluster => DeploymentTargetBootstrapMode.InCluster,
            DeploymentTargetBootstrapMode.Kubeconfig => DeploymentTargetBootstrapMode.Kubeconfig,
            _ => throw new InvalidOperationException(
                "The deployment-target bootstrap mode is not supported.")
        };
        if (mode == DeploymentTargetBootstrapMode.InCluster)
            return new InClusterConnectionInput();

        var path = ResolveKubeconfigPath(options.Bootstrap.KubeconfigPath);
        string kubeconfig;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > 1_048_576)
                throw new InvalidOperationException();
            kubeconfig = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The development bootstrap kubeconfig could not be read.");
        }

        return new KubeconfigConnectionInput(
            kubeconfig,
            options.Bootstrap.KubeconfigContext);
    }

    private DeploymentTargetWorkloadConfiguration CreateWorkloadConfiguration()
    {
        var configuredIngress = options.Bootstrap.WorkloadConfiguration.Ingress;
        var ingress = configuredIngress is null
            ? null
            : DeploymentTargetIngressConfiguration.Create(
                configuredIngress.IngressClassName,
                configuredIngress.ClusterIssuerName);
        return DeploymentTargetWorkloadConfiguration.Create(ingress);
    }

    private static string ResolveKubeconfigPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
            {
                throw new InvalidOperationException(
                    "Development bootstrap requires an absolute kubeconfig path.");
            }

            return configuredPath;
        }

        var environmentValue = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            var candidates = environmentValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException(
                    "Development bootstrap requires one explicit kubeconfig file, not a merged KUBECONFIG list.");
            }

            if (!Path.IsPathFullyQualified(candidates[0]))
            {
                throw new InvalidOperationException(
                    "Development bootstrap requires an absolute KUBECONFIG file path.");
            }

            return candidates[0];
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            throw new InvalidOperationException("A development bootstrap kubeconfig path is required.");
        return Path.Combine(userProfile, ".kube", "config");
    }

    private Task<Result> PersistFirstTargetAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken) =>
        dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var existing = await dbContext.DeploymentTargets
                .Find(
                    session,
                    Builders<DeploymentTarget>.Filter.Eq(
                        item => item.DeletionRequestedAtUtc,
                        null))
                .SortBy(item => item.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                return await BindDefaultSettingsAsync(session, existing, ct);
            }

            await dbContext.DeploymentTargets.InsertOneAsync(session, target, cancellationToken: ct);
            await dbContext.DeploymentTargetSettings.InsertOneAsync(
                session,
                DeploymentTargetSettings.Create(target.Id, target.CreatedAtUtc),
                cancellationToken: ct);
            return Result.Success();
        }, cancellationToken: cancellationToken);

    private Task<Result> EnsureDefaultSettingsAsync(
        string targetId,
        CancellationToken cancellationToken) =>
        dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var target = await dbContext.DeploymentTargets
                .Find(session, item => item.Id == targetId)
                .FirstOrDefaultAsync(ct);
            return target is null
                ? Failures.Conflict("The deployment target changed while its default binding was created.")
                : await BindDefaultSettingsAsync(session, target, ct);
        }, cancellationToken: cancellationToken);

    private async Task<Result> BindDefaultSettingsAsync(
        IClientSessionHandle session,
        DeploymentTarget target,
        CancellationToken cancellationToken)
    {
        if (target.DeletionRequestedAtUtc is not null)
            return Failures.Conflict("A deployment target pending deletion cannot become the default target.");

        var existing = await dbContext.DeploymentTargetSettings
            .Find(session, settings => settings.Id == DeploymentTargetSettings.SingletonId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing.DefaultDeploymentTargetId == target.Id
                ? Result.Success()
                : Failures.Conflict("A different default deployment target is already configured.");
        }

        var updatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (updatedAtUtc < target.UpdatedAtUtc)
            updatedAtUtc = target.UpdatedAtUtc;
        var fenced = await dbContext.DeploymentTargets.UpdateOneAsync(
            session,
            Builders<DeploymentTarget>.Filter.And(
                Builders<DeploymentTarget>.Filter.Eq(item => item.Id, target.Id),
                Builders<DeploymentTarget>.Filter.Eq(item => item.Revision, target.Revision),
                Builders<DeploymentTarget>.Filter.Eq(
                    item => item.DeletionRequestedAtUtc,
                    null)),
            Builders<DeploymentTarget>.Update
                .Set(item => item.Revision, checked(target.Revision + 1))
                .Set(item => item.UpdatedAtUtc, updatedAtUtc),
            cancellationToken: cancellationToken);
        if (fenced.ModifiedCount != 1)
            return Failures.Conflict("The deployment target changed while its default binding was created.");

        await dbContext.DeploymentTargetSettings.InsertOneAsync(
            session,
            DeploymentTargetSettings.Create(target.Id, updatedAtUtc),
            cancellationToken: cancellationToken);
        return Result.Success();
    }

    private async Task<BootstrapState> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.DeploymentTargetSettings
            .Find(item => item.Id == DeploymentTargetSettings.SingletonId)
            .FirstOrDefaultAsync(cancellationToken);
        var firstTarget = await dbContext.DeploymentTargets
            .Find(Builders<DeploymentTarget>.Filter.Eq(
                item => item.DeletionRequestedAtUtc,
                null))
            .SortBy(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var defaultTargetExists = settings is not null &&
            await dbContext.DeploymentTargets.CountDocumentsAsync(
                item => item.Id == settings.DefaultDeploymentTargetId,
                cancellationToken: cancellationToken) == 1;
        return new BootstrapState(settings, firstTarget, defaultTargetExists);
    }

    private sealed record BootstrapState(
        DeploymentTargetSettings? Settings,
        DeploymentTarget? FirstTarget,
        bool DefaultTargetExists);
}
