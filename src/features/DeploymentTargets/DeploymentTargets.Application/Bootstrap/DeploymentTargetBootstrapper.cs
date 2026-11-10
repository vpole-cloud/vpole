using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Bootstrap;

public delegate Task BootstrapDeploymentTargets(CancellationToken cancellationToken = default);

internal sealed class DeploymentTargetBootstrapper(
    LoadDeploymentTargetBootstrapState loadBootstrapState,
    EnsureBootstrapDefaultDeploymentTarget ensureBootstrapDefaultDeploymentTarget,
    PrepareDeploymentTargetConnection prepareDeploymentTargetConnection,
    ProbeDeploymentTargetConnection probeDeploymentTargetConnection,
    DeploymentTargetOptions options,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<DeploymentTargetBootstrapper> logger
)
{
    public async Task InvokeAsync(CancellationToken cancellationToken = default)
    {
        var current = await loadBootstrapState(cancellationToken);
        if (current.Settings is not null)
        {
            if (current.DefaultTargetExists)
                return;

            throw new InvalidOperationException("Deployment-target settings reference a target that does not exist.");
        }

        if (current.FirstTarget is not null)
        {
            var ensured = await ensureBootstrapDefaultDeploymentTarget(
                new BindExistingDeploymentTargetAsDefault(current.FirstTarget.Id),
                cancellationToken);
            if (ensured is BootstrapDefaultDeploymentTargetEnsured)
                return;

            current = await loadBootstrapState(cancellationToken);
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
        var preparedResult = await prepareDeploymentTargetConnection(
            targetId,
            connectionVersion: 1,
            input,
            cancellationToken);
        if (preparedResult is not PreparedDeploymentTargetConnection)
            throw new InvalidOperationException("The local deployment-target connection could not be prepared.");

        var prepared = (PreparedDeploymentTargetConnection)preparedResult.Value;
        using (prepared)
        {
            var observation = await probeDeploymentTargetConnection(
                prepared.Candidate,
                prepared.Connection.Version,
                cancellationToken: cancellationToken);
            if (!observation.IsUsable || observation.ClusterIdentity is null)
                throw new InvalidOperationException(
                    $"The local deployment target could not be validated ({observation.Health.FailureCode}).");

            var createdAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var target = DeploymentTargetFactory.Create(
                options.Bootstrap.TechnicalName,
                options.Bootstrap.DisplayName,
                observation.ClusterIdentity,
                prepared.Connection,
                observation.Health,
                observation.Capabilities,
                CreateWorkloadConfiguration(),
                enabled: true,
                acceptsWorkloads: true,
                new DeploymentTargetFactory.CreateOptions(targetId, createdAtUtc));

            var persisted = await ensureBootstrapDefaultDeploymentTarget(
                new CreateFirstDeploymentTargetAsDefault(target),
                cancellationToken);
            if (persisted is BootstrapDefaultDeploymentTargetEnsured)
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
        current = await loadBootstrapState(cancellationToken);
        if (current.Settings is not null && current.DefaultTargetExists)
            return;

        throw new InvalidOperationException("The default deployment target could not be bootstrapped.");
    }

    private async Task<DeploymentTargetConnectionInput> CreateBootstrapInputAsync(CancellationToken cancellationToken)
    {
        var mode = options.Bootstrap.Mode switch
        {
            DeploymentTargetBootstrapMode.Auto when environment.IsDevelopment() => DeploymentTargetBootstrapMode
                .Kubeconfig,
            DeploymentTargetBootstrapMode.Auto => DeploymentTargetBootstrapMode.InCluster,
            DeploymentTargetBootstrapMode.InCluster => DeploymentTargetBootstrapMode.InCluster,
            DeploymentTargetBootstrapMode.Kubeconfig => DeploymentTargetBootstrapMode.Kubeconfig,
            _ => throw new InvalidOperationException("The deployment-target bootstrap mode is not supported."),
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            throw new InvalidOperationException("The development bootstrap kubeconfig could not be read.");
        }

        return new KubeconfigConnectionInput(kubeconfig, options.Bootstrap.KubeconfigContext);
    }

    private DeploymentTargetWorkloadConfiguration CreateWorkloadConfiguration()
    {
        var configuredIngress = options.Bootstrap.WorkloadConfiguration.Ingress;
        var ingress = configuredIngress is null
            ? null
            : DeploymentTargetIngressConfiguration.Create(
                configuredIngress.IngressClassName,
                configuredIngress.ClusterIssuerName,
                configuredIngress.PublicBaseDomain);
        return DeploymentTargetWorkloadConfiguration.Create(
            ingress,
            options.Bootstrap.WorkloadConfiguration.NetworkSources.ToDomain());
    }

    private static string ResolveKubeconfigPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
                throw new InvalidOperationException("Development bootstrap requires an absolute kubeconfig path.");

            return configuredPath;
        }

        var environmentValue = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            var candidates = environmentValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (candidates.Length != 1)
                throw new InvalidOperationException(
                    "Development bootstrap requires one explicit kubeconfig file, not a merged KUBECONFIG list.");

            if (!Path.IsPathFullyQualified(candidates[0]))
                throw new InvalidOperationException("Development bootstrap requires an absolute KUBECONFIG file path.");

            return candidates[0];
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            throw new InvalidOperationException("A development bootstrap kubeconfig path is required.");
        return Path.Combine(userProfile, ".kube", "config");
    }
}
