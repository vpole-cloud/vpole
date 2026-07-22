using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.DeploymentTargets.Domain;

public sealed record DeploymentTarget
{
    [BsonConstructor]
    public DeploymentTarget(
        string id,
        string displayName,
        string clusterIdentity,
        DeploymentTargetConnection connection,
        DeploymentTargetHealth health,
        DeploymentTargetCapabilities capabilities,
        long revision,
        long referenceFence,
        bool enabled,
        bool acceptsWorkloads,
        DateTime? deletionRequestedAtUtc,
        DateTime createdAtUtc,
        DateTime updatedAtUtc)
    {
        Id = DeploymentTargetValueGuard.ObjectId(id, nameof(id));
        DisplayName = DeploymentTargetValueGuard.Required(
            displayName,
            nameof(displayName),
            DeploymentTargetValueGuard.MaximumDisplayNameLength);
        ClusterIdentity = DeploymentTargetValueGuard.Required(
            clusterIdentity,
            nameof(clusterIdentity),
            DeploymentTargetValueGuard.MaximumClusterIdentityLength);
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Health = health ?? throw new ArgumentNullException(nameof(health));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Revision = DeploymentTargetValueGuard.Positive(revision, nameof(revision));
        ReferenceFence = DeploymentTargetValueGuard.NonNegative(referenceFence, nameof(referenceFence));
        CreatedAtUtc = DeploymentTargetValueGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = DeploymentTargetValueGuard.Utc(updatedAtUtc, nameof(updatedAtUtc));
        DeletionRequestedAtUtc = deletionRequestedAtUtc is null
            ? null
            : DeploymentTargetValueGuard.Utc(deletionRequestedAtUtc.Value, nameof(deletionRequestedAtUtc));

        if (UpdatedAtUtc < CreatedAtUtc)
            throw new ArgumentException("The update time cannot precede the creation time.", nameof(updatedAtUtc));
        if (DeletionRequestedAtUtc > UpdatedAtUtc)
        {
            throw new ArgumentException(
                "The deletion request time cannot be later than the update time.",
                nameof(deletionRequestedAtUtc));
        }
        if (!enabled && acceptsWorkloads)
        {
            throw new ArgumentException(
                "A disabled deployment target cannot accept new workloads.",
                nameof(acceptsWorkloads));
        }
        if (DeletionRequestedAtUtc is not null && acceptsWorkloads)
        {
            throw new ArgumentException(
                "A deployment target pending deletion cannot accept new workloads.",
                nameof(acceptsWorkloads));
        }
        if (health.ConnectionVersion != connection.Version ||
            capabilities.ConnectionVersion != connection.Version)
        {
            throw new ArgumentException(
                "Connection, health, and capability versions must match.",
                nameof(connection));
        }

        Enabled = enabled;
        AcceptsWorkloads = acceptsWorkloads;
    }

    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; }
    public string DisplayName { get; }
    public string ClusterIdentity { get; }
    public DeploymentTargetConnection Connection { get; }
    public DeploymentTargetHealth Health { get; }
    public DeploymentTargetCapabilities Capabilities { get; }
    public long Revision { get; }
    public long ReferenceFence { get; }
    public bool Enabled { get; }
    public bool AcceptsWorkloads { get; }
    public DateTime? DeletionRequestedAtUtc { get; }
    public DateTime CreatedAtUtc { get; }
    public DateTime UpdatedAtUtc { get; }

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
            updatedAtUtc);
    }

    public DeploymentTarget Reconfigure(
        string displayName,
        bool enabled,
        bool acceptsWorkloads,
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
        if (string.Equals(DisplayName, normalizedDisplayName, StringComparison.Ordinal) &&
            Enabled == enabled &&
            AcceptsWorkloads == acceptsWorkloads)
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
            checked(Revision + 1),
            ReferenceFence,
            Enabled,
            acceptsWorkloads: false,
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

        var expectedVersion = checked(Connection.Version + 1);
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
        bool enabled,
        bool acceptsWorkloads,
        CreateOptions options) =>
        new(
            options.Id,
            displayName,
            clusterIdentity,
            connection,
            health,
            capabilities,
            revision: 1,
            referenceFence: 0,
            enabled,
            acceptsWorkloads,
            deletionRequestedAtUtc: null,
            options.CreatedAtUtc,
            options.CreatedAtUtc);
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

    public static long NonNegative(long value, string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName, "The value cannot be negative.");
        return value;
    }

    public static string ObjectId(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, 24);
        if (!MongoDB.Bson.ObjectId.TryParse(normalized, out _))
            throw new ArgumentException("The value must be a MongoDB ObjectId.", parameterName);
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
