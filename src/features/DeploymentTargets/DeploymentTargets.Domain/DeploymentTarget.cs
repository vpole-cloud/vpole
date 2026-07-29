using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.DeploymentTargets.Domain;

public sealed record DeploymentTarget(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string DisplayName,
    string ClusterIdentity,
    DeploymentTargetConnection Connection,
    DeploymentTargetHealth Health,
    DeploymentTargetCapabilities Capabilities,
    DeploymentTargetWorkloadConfiguration WorkloadConfiguration,
    long Revision,
    long ReferenceFence,
    bool Enabled,
    bool AcceptsWorkloads,
    DateTime? DeletionRequestedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    [BsonIgnore]
    public long NextConnectionVersion => checked(Connection.Version + 1);

    public DeploymentTarget UpdateMetadata(string displayName, DateTime updatedAtUtc)
    {
        var normalizedDisplayName = DeploymentTargetValueGuard.Required(
            displayName,
            nameof(displayName),
            DeploymentTargetValueGuard.MaximumDisplayNameLength);
        if (string.Equals(DisplayName, normalizedDisplayName, StringComparison.Ordinal))
            return this;

        return Reconfigured(
            normalizedDisplayName,
            Connection,
            Enabled,
            AcceptsWorkloads,
            Health,
            Capabilities,
            WorkloadConfiguration,
            updatedAtUtc);
    }

    public DeploymentTarget SetWorkloadAdmission(bool acceptsWorkloads, DateTime updatedAtUtc)
    {
        if (acceptsWorkloads && !Enabled)
            throw new InvalidOperationException("A disabled deployment target cannot accept new workloads.");
        if (acceptsWorkloads && DeletionRequestedAtUtc is not null)
            throw new InvalidOperationException("A deployment target pending deletion cannot accept new workloads.");
        if (AcceptsWorkloads == acceptsWorkloads)
            return this;

        return Reconfigured(
            DisplayName,
            Connection,
            Enabled,
            acceptsWorkloads,
            Health,
            Capabilities,
            WorkloadConfiguration,
            updatedAtUtc);
    }

    public DeploymentTarget SetEnabled(bool enabled, DateTime updatedAtUtc)
    {
        var acceptsWorkloads = enabled && AcceptsWorkloads;
        if (Enabled == enabled && AcceptsWorkloads == acceptsWorkloads)
            return this;

        return Reconfigured(
            DisplayName,
            Connection,
            enabled,
            acceptsWorkloads,
            Health,
            Capabilities,
            WorkloadConfiguration,
            updatedAtUtc);
    }

    public DeploymentTarget Reconfigure(
        string displayName,
        bool enabled,
        bool acceptsWorkloads,
        DeploymentTargetIngressConfiguration? ingress,
        DateTime updatedAtUtc)
    {
        var normalizedDisplayName = DeploymentTargetValueGuard.Required(
            displayName,
            nameof(displayName),
            DeploymentTargetValueGuard.MaximumDisplayNameLength);
        if (!enabled && acceptsWorkloads)
            throw new InvalidOperationException("A disabled deployment target cannot accept new workloads.");
        if (acceptsWorkloads && DeletionRequestedAtUtc is not null)
            throw new InvalidOperationException("A deployment target pending deletion cannot accept new workloads.");
        var workloadConfiguration = WorkloadConfiguration.Reconfigure(ingress);
        if (string.Equals(DisplayName, normalizedDisplayName, StringComparison.Ordinal) &&
            Enabled == enabled &&
            AcceptsWorkloads == acceptsWorkloads &&
            Equals(WorkloadConfiguration, workloadConfiguration))
        {
            return this;
        }

        return Reconfigured(
            normalizedDisplayName,
            Connection,
            enabled,
            acceptsWorkloads,
            Health,
            Capabilities,
            workloadConfiguration,
            updatedAtUtc);
    }

    public DeploymentTarget RequestDeletion(DateTime requestedAtUtc)
    {
        if (DeletionRequestedAtUtc is not null)
            return this;

        var normalizedRequestedAtUtc = DeploymentTargetValueGuard.Utc(
            requestedAtUtc,
            nameof(requestedAtUtc));
        if (normalizedRequestedAtUtc < UpdatedAtUtc)
            normalizedRequestedAtUtc = UpdatedAtUtc;
        return new DeploymentTarget(
            Id,
            DisplayName,
            ClusterIdentity,
            Connection,
            Health,
            Capabilities,
            WorkloadConfiguration,
            checked(Revision + 1),
            ReferenceFence,
            Enabled,
            AcceptsWorkloads: false,
            normalizedRequestedAtUtc,
            CreatedAtUtc,
            normalizedRequestedAtUtc);
    }

    public DeploymentTarget ReplaceConnectionCredential(
        string validatedClusterIdentity,
        DeploymentTargetConnection replacement,
        DateTime updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var normalizedIdentity = DeploymentTargetValueGuard.Required(
            validatedClusterIdentity,
            nameof(validatedClusterIdentity),
            DeploymentTargetValueGuard.MaximumClusterIdentityLength);
        if (!string.Equals(ClusterIdentity, normalizedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Replacement credentials must resolve to the same Kubernetes cluster identity.");
        }

        var expectedVersion = NextConnectionVersion;
        if (replacement.Version != expectedVersion)
        {
            throw new InvalidOperationException(
                "Replacement credentials must advance the connection version exactly once.");
        }

        return Reconfigured(
            DisplayName,
            replacement,
            Enabled,
            AcceptsWorkloads,
            DeploymentTargetHealth.Unknown(replacement.Version),
            DeploymentTargetCapabilities.Unknown(replacement.Version),
            WorkloadConfiguration,
            updatedAtUtc);
    }

    /// <summary>
    /// Applies health and capability observations only to the exact configuration snapshot that was validated.
    /// Persistence must use the same revision and connection-version values in its compare-and-set filter.
    /// Observational updates deliberately do not advance the aggregate revision or configuration update time.
    /// </summary>
    public DeploymentTarget ApplyValidationObservation(
        long expectedRevision,
        long expectedConnectionVersion,
        DeploymentTargetHealth health,
        DeploymentTargetCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (Revision != expectedRevision || Connection.Version != expectedConnectionVersion)
        {
            throw new InvalidOperationException(
                "The deployment target changed while its connection was being validated.");
        }
        if (health.ConnectionVersion != expectedConnectionVersion ||
            capabilities.ConnectionVersion != expectedConnectionVersion)
        {
            throw new ArgumentException(
                "Validation observations must match the validated connection version.",
                nameof(health));
        }

        return new DeploymentTarget(
            Id,
            DisplayName,
            ClusterIdentity,
            Connection,
            health,
            capabilities,
            WorkloadConfiguration,
            Revision,
            ReferenceFence,
            Enabled,
            AcceptsWorkloads,
            DeletionRequestedAtUtc,
            CreatedAtUtc,
            UpdatedAtUtc);
    }

    private DeploymentTarget Reconfigured(
        string displayName,
        DeploymentTargetConnection connection,
        bool enabled,
        bool acceptsWorkloads,
        DeploymentTargetHealth health,
        DeploymentTargetCapabilities capabilities,
        DeploymentTargetWorkloadConfiguration workloadConfiguration,
        DateTime updatedAtUtc)
    {
        var normalizedUpdatedAtUtc = DeploymentTargetValueGuard.Utc(updatedAtUtc, nameof(updatedAtUtc));
        if (normalizedUpdatedAtUtc < UpdatedAtUtc)
            throw new ArgumentException("The update time cannot move backwards.", nameof(updatedAtUtc));

        return new DeploymentTarget(
            Id,
            displayName,
            ClusterIdentity,
            connection,
            health,
            capabilities,
            workloadConfiguration,
            checked(Revision + 1),
            ReferenceFence,
            enabled,
            acceptsWorkloads,
            DeletionRequestedAtUtc,
            CreatedAtUtc,
            normalizedUpdatedAtUtc);
    }
}

public static class DeploymentTargetFactory
{
    public sealed record CreateOptions(string Id, DateTime CreatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static DeploymentTarget Create(
        string displayName,
        string clusterIdentity,
        DeploymentTargetConnection connection,
        DeploymentTargetHealth health,
        DeploymentTargetCapabilities capabilities,
        DeploymentTargetWorkloadConfiguration workloadConfiguration,
        bool enabled,
        bool acceptsWorkloads,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(workloadConfiguration);
        if (workloadConfiguration.Revision != 1)
        {
            throw new ArgumentException(
                "A new deployment target workload configuration must start at revision 1.",
                nameof(workloadConfiguration));
        }

        var normalizedId = DeploymentTargetValueGuard.ObjectId(options.Id, nameof(options.Id));
        var normalizedDisplayName = DeploymentTargetValueGuard.Required(
            displayName,
            nameof(displayName),
            DeploymentTargetValueGuard.MaximumDisplayNameLength);
        var normalizedClusterIdentity = DeploymentTargetValueGuard.Required(
            clusterIdentity,
            nameof(clusterIdentity),
            DeploymentTargetValueGuard.MaximumClusterIdentityLength);
        var normalizedCreatedAtUtc = DeploymentTargetValueGuard.Utc(
            options.CreatedAtUtc,
            nameof(options.CreatedAtUtc));
        var normalizedWorkloadConfiguration = DeploymentTargetWorkloadConfiguration.Create(
            workloadConfiguration.Ingress);

        if (!enabled && acceptsWorkloads)
        {
            throw new ArgumentException(
                "A disabled deployment target cannot accept new workloads.",
                nameof(acceptsWorkloads));
        }
        if (health.ConnectionVersion != connection.Version ||
            capabilities.ConnectionVersion != connection.Version)
        {
            throw new ArgumentException(
                "Connection, health, and capability versions must match.",
                nameof(connection));
        }

        return new DeploymentTarget(
            normalizedId,
            normalizedDisplayName,
            normalizedClusterIdentity,
            connection,
            health,
            capabilities,
            normalizedWorkloadConfiguration,
            Revision: 1,
            ReferenceFence: 0,
            enabled,
            acceptsWorkloads,
            DeletionRequestedAtUtc: null,
            normalizedCreatedAtUtc,
            normalizedCreatedAtUtc);
    }
}

internal static class DeploymentTargetValueGuard
{
    public const int MaximumDisplayNameLength = 80;
    public const int MaximumClusterIdentityLength = 512;
    public const int MaximumContextNameLength = 253;
    public const int MaximumServerUrlLength = 2048;
    public const int MaximumCertificateAuthorityDataLength = 1_048_576;
    public const int MaximumKubernetesVersionLength = 128;
    public const int MaximumFailureCodeLength = 128;
    public const int MaximumFailureMessageLength = 512;

    public static string Required(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value is required.", parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The value must be {maximumLength} characters or less.", parameterName);
        return normalized;
    }

    public static long Positive(long value, string parameterName)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(parameterName, "The value must be positive.");
        return value;
    }

    public static string ObjectId(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, 24);
        if (!MongoDB.Bson.ObjectId.TryParse(normalized, out _))
            throw new ArgumentException("The value must be a MongoDB ObjectId.", parameterName);
        return normalized;
    }

    public static string KubernetesResourceName(string? value, string parameterName)
    {
        var normalized = Required(
            value,
            parameterName,
            DeploymentTargetIngressConfiguration.MaximumNameLength);
        if (!DeploymentTargetIngressConfiguration.IsValidKubernetesResourceName(normalized))
        {
            throw new ArgumentException(
                "The value must be a valid Kubernetes resource name.",
                parameterName);
        }

        return normalized;
    }

    public static DateTime Utc(DateTime value, string parameterName)
    {
        if (value == default)
            throw new ArgumentException("The timestamp is required.", parameterName);
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public static string HttpsServerUrl(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, MaximumServerUrlLength);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "The Kubernetes server URL must be an absolute HTTPS URL without credentials, query, or fragment.",
                parameterName);
        }

        return normalized.TrimEnd('/');
    }
}
