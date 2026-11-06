using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;
using Pole.Features.Shared.Models;

namespace Pole.Features.WebServices.Domain;

public sealed record WebService(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)] string Id,
    string TenantId,
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    WebServiceSpec Spec,
    WebServiceExposure Exposure,
    long ActiveRevision,
    ImmutableHashSet<string> Tags,
    ImmutableList<ServiceEvent> Events,
    long Revision,
    WebServiceLifecycle Lifecycle,
    [property: BsonIgnoreIfNull] WebServiceDeclaredPlacement? DeclaredPlacement,
    long PlanningFence) : ITenantOwned
{
    public WebService BindDeclaredPlacement(WebServiceDeclaredPlacement placement)
    {
        if (DeclaredPlacement is not null && DeclaredPlacement.DeploymentTargetId != placement.DeploymentTargetId)
            throw new InvalidOperationException("A declared service placement cannot change its target identity.");
        return this with { DeclaredPlacement = placement };
    }

    public WebService ActivatePrepared(WebServiceSpec spec, IEnumerable<string> tags, WebServiceExposure exposure)
    {
        WebServiceImageFactory.Validate(spec.Image);
        if (spec.Image.ResolvedImage is null)
            throw new InvalidOperationException("Activation requires an image artifact.");

        if (Lifecycle is not PreparedWebServiceLifecycle)
            return UpdateDesiredState(spec, tags, exposure);

        EnsureImageAuthority(spec.Image);
        return this with
        {
            Spec = spec,
            Tags = WebServiceFactory.NormalizeTags(tags),
            Exposure = exposure,
            Lifecycle = new ActiveWebServiceLifecycle(),
            ActiveRevision = checked(ActiveRevision + 1),
            Revision = checked(Revision + 1)
        };
    }

    public WebService AcceptConfigurationAwaitingArtifact(
        WebServiceSpec spec,
        IEnumerable<string> tags,
        WebServiceExposure exposure)
    {
        if (Lifecycle is not (PreparedWebServiceLifecycle or AwaitingFirstArtifactWebServiceLifecycle))
            throw new InvalidOperationException("Only an unactivated build service can await its first artifact.");
        if (spec.Image is not PendingSourceBuildImage)
            throw new InvalidOperationException("An accepted build configuration identity is required.");

        WebServiceImageFactory.Validate(spec.Image);
        EnsureImageAuthority(spec.Image);
        var normalizedTags = WebServiceFactory.NormalizeTags(tags);
        if (Lifecycle is AwaitingFirstArtifactWebServiceLifecycle && Spec.HasSameConfiguration(spec) &&
            Tags.SetEquals(normalizedTags) && Exposure == exposure)
            return this;

        return this with
        {
            Spec = spec,
            Tags = normalizedTags,
            Exposure = exposure,
            Lifecycle = new AwaitingFirstArtifactWebServiceLifecycle(),
            ActiveRevision = checked(ActiveRevision + 1),
            Revision = checked(Revision + 1)
        };
    }

    public WebService PromoteArtifact(DeliveredSourceBuildImage artifact)
    {
        if (Lifecycle is not (AwaitingFirstArtifactWebServiceLifecycle or ActiveWebServiceLifecycle))
            throw new InvalidOperationException("The service must have accepted its build configuration.");

        WebServiceImageFactory.Validate(artifact);
        if (Spec.Image.SourceBuildConfigurationId != artifact.ConfigurationId)
            throw new InvalidOperationException("The artifact belongs to another build configuration.");
        if (Spec.Image == artifact)
            return this;
        if (Spec.Image is DeliveredSourceBuildImage previous && artifact.Generation <= previous.Generation)
            throw new InvalidOperationException("An artifact cannot replace the same or a newer build generation.");

        return this with
        {
            Spec = Spec with { Image = artifact },
            Lifecycle = new ActiveWebServiceLifecycle(),
            ActiveRevision = checked(ActiveRevision + 1),
            Revision = checked(Revision + 1)
        };
    }

    public WebService UpdateDesiredState(WebServiceSpec spec, IEnumerable<string> tags)
        => UpdateDesiredState(spec, tags, Exposure);

    public WebService UpdateDesiredState(
        WebServiceSpec spec,
        IEnumerable<string> tags,
        WebServiceExposure exposure)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(exposure);

        WebServiceImageFactory.Validate(spec.Image);
        EnsureImageAuthority(spec.Image);
        if (spec.Image.ResolvedImage is null)
            throw new InvalidOperationException("An active service requires an image artifact.");

        var normalizedTags = WebServiceFactory.NormalizeTags(tags);
        return Spec.HasSameConfiguration(spec) && Tags.SetEquals(normalizedTags) && Equals(Exposure, exposure)
            ? this
            : this with
            {
                Spec = spec,
                Exposure = exposure,
                Tags = normalizedTags,
                ActiveRevision = checked(ActiveRevision + 1),
                Revision = checked(Revision + 1)
            };
    }

    /// <summary>
    /// Advances the complete logical desired state when only placement/binding state changed.
    /// The placement documents are mutated by the application transaction; the logical revision
    /// remains the fence consumers use to associate those exact physical snapshots with a release.
    /// </summary>
    public WebService AdvancePlacementDesiredState()
    {
        if (Lifecycle is not (ActiveWebServiceLifecycle or AwaitingFirstArtifactWebServiceLifecycle))
            throw new InvalidOperationException("Placement requires an accepted service configuration.");
        return this with
        {
            ActiveRevision = checked(ActiveRevision + 1),
            Revision = checked(Revision + 1)
        };
    }

    public WebService Redeploy()
    {
        EnsureActive();
        return AdvancePlacementDesiredState();
    }

    public WebService Rollback(WebServiceSpec spec)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(spec);
        EnsureImageAuthority(spec.Image);
        if (Spec.Image is not ImageInfo || spec.Image is not ImageInfo)
            throw new InvalidOperationException("Build artifacts are changed through verified promotion.");

        return this with
        {
            Spec = spec,
            ActiveRevision = checked(ActiveRevision + 1),
            Revision = checked(Revision + 1)
        };
    }

    public WebService BeginDeletion(DateTime requestedAtUtc)
    {
        if (Lifecycle is DeletingWebServiceLifecycle)
            return this;

        return this with
        {
            Lifecycle = new DeletingWebServiceLifecycle(WebServiceFactory.Utc(requestedAtUtc)),
            Revision = checked(Revision + 1)
        };
    }

    private void EnsureImageAuthority(WebServiceImage next)
    {
        if (Spec.Image is UnboundSourceBuildImage && Lifecycle is PreparedWebServiceLifecycle &&
            next is PendingSourceBuildImage)
            return;
        if (Spec.Image is ImageInfo && next is ImageInfo)
            return;
        if (Spec.Image.SourceBuildConfigurationId is { } configurationId &&
            next.SourceBuildConfigurationId == configurationId)
        {
            if (Spec.Image is PendingSourceBuildImage && next is not PendingSourceBuildImage ||
                Spec.Image is DeliveredSourceBuildImage && Spec.Image != next)
                throw new InvalidOperationException("Build artifacts are changed through verified promotion.");
            return;
        }

        throw new InvalidOperationException("The image authority cannot change after creation.");
    }

    private void EnsureActive()
    {
        if (Lifecycle is not ActiveWebServiceLifecycle)
            throw new InvalidOperationException("Only an active web service can perform this operation.");
    }
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(PreparedWebServiceLifecycle), typeof(AwaitingFirstArtifactWebServiceLifecycle),
    typeof(ActiveWebServiceLifecycle), typeof(DeletingWebServiceLifecycle))]
public closed record WebServiceLifecycle;

public sealed record PreparedWebServiceLifecycle : WebServiceLifecycle;

public sealed record AwaitingFirstArtifactWebServiceLifecycle : WebServiceLifecycle;

public sealed record ActiveWebServiceLifecycle : WebServiceLifecycle;

public sealed record DeletingWebServiceLifecycle(DateTime RequestedAtUtc) : WebServiceLifecycle;

/// <summary>Configuration shared by every physical deployment of a logical web service.</summary>
public sealed record WebServiceSpec(
    WebServiceImage Image,
    WebServiceConfig Config,
    AutoscalingSpec? Autoscaling,
    ProbeSpec Probes)
{
    // Immutable collection instances change at HTTP and persistence boundaries.
    public bool HasSameConfiguration(WebServiceSpec other) =>
        Image == other.Image &&
        Autoscaling == other.Autoscaling &&
        Probes == other.Probes &&
        Config.PortMappings.SequenceEqual(other.Config.PortMappings) &&
        SameEntries(Config.EnvironmentVariables, other.Config.EnvironmentVariables) &&
        SameEntries(Config.ConnectionEnvironmentReferences, other.Config.ConnectionEnvironmentReferences) &&
        SameEntries(Config.SecretEnvironmentReferences, other.Config.SecretEnvironmentReferences);

    private static bool SameEntries<T>(ImmutableDictionary<string, T> left, ImmutableDictionary<string, T> right) =>
        left.Count == right.Count &&
        left.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(pair => pair.Key, StringComparer.Ordinal));
}

/// <summary>
/// Logical exposure policy, deliberately not a concrete hostname. Deployment hostname ownership
/// and concrete FQDNs are held by Domains/ResourceDeployments.
/// </summary>
public enum WebServiceExposureMode
{
    Private,
    Public
}

public sealed record WebServiceExposure(
    [property: BsonRepresentation(BsonType.String)] WebServiceExposureMode Mode,
    string GeneratedHostnameLabelPrefix)
{
    public const string DefaultGeneratedHostnameLabelPrefix = "webservice";

    public string GeneratedHostnameLabel(string resourceId) =>
        $"{GeneratedHostnameLabelPrefix}-{resourceId}";

    public static WebServiceExposure Create(
        WebServiceExposureMode mode = WebServiceExposureMode.Private,
        string? generatedHostnameLabelPrefix = null)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        return new(mode, string.IsNullOrWhiteSpace(generatedHostnameLabelPrefix)
            ? DefaultGeneratedHostnameLabelPrefix
            : generatedHostnameLabelPrefix.Trim().ToLowerInvariant());
    }
}

public sealed record AutoscalingSpec(
    int MinReplicas,
    int MaxReplicas,
    AutoscalingMetric Metric);
public sealed record AutoscalingMetric(
    string Type,
    int TargetAverageUtilization);
public sealed record WebServiceConfig
{
    public required ImmutableDictionary<string, string> EnvironmentVariables { get; init; }
    public required ImmutableList<PortMapping> PortMappings { get; init; }
    public ImmutableDictionary<string, ConnectionEnvironmentReference> ConnectionEnvironmentReferences
    {
        get => field ?? ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty;
        init;
    } = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty;
    public ImmutableDictionary<string, SecretEnvironmentReference> SecretEnvironmentReferences
    {
        get => field ?? ImmutableDictionary<string, SecretEnvironmentReference>.Empty;
        init;
    } = ImmutableDictionary<string, SecretEnvironmentReference>.Empty;
}

public sealed record ConnectionEnvironmentReference(
    string ConnectionId,
    string ValueKind,
    string? ConfigurationKey);

public sealed record SecretEnvironmentReference(
    string SecretId,
    string Key);
public sealed record PortMapping(
    int ServicePort,
    string TargetPort,
    string Protocol,
    string Name);

public static class WebServiceFactory
{
    public sealed record CreateOptions(
    string Id,
    long ActiveRevision,
    ImmutableHashSet<string> Tags,
    ImmutableList<ServiceEvent> Events,
    long Revision,
    WebServiceLifecycle Lifecycle);

    public static CreateOptions DefaultOptions => new(
        ObjectId.GenerateNewId().ToString(), 1, ImmutableHashSet<string>.Empty,
        ImmutableList<ServiceEvent>.Empty, 1, new ActiveWebServiceLifecycle());

    public static WebService Create(
        string name,
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        WebServiceSpec spec,
        WebServiceExposure exposure,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(exposure);
        ArgumentNullException.ThrowIfNull(options);
        WebServiceImageFactory.Validate(spec.Image);
        if (options.Lifecycle is ActiveWebServiceLifecycle && spec.Image.ResolvedImage is null)
            throw new ArgumentException("An active service requires an image artifact.", nameof(spec));
        if (options.Lifecycle is AwaitingFirstArtifactWebServiceLifecycle && spec.Image is not PendingSourceBuildImage)
            throw new ArgumentException("Awaiting services require a bound pending image.", nameof(spec));
        if (spec.Image is UnboundSourceBuildImage && options.Lifecycle is not PreparedWebServiceLifecycle)
            throw new ArgumentException(
                "Only prepared services can have an unbound build configuration.", nameof(spec));

        return new WebService(
            RequiredObjectId(options.Id, nameof(options.Id)),
            RequiredObjectId(tenantId, nameof(tenantId)),
            Required(name, nameof(name), 30),
            RequiredObjectId(resourceGroupId, nameof(resourceGroupId)),
            RequiredObjectId(isolationScopeId, nameof(isolationScopeId)),
            spec,
            WebServiceExposure.Create(exposure.Mode, exposure.GeneratedHostnameLabelPrefix),
            Positive(options.ActiveRevision, nameof(options.ActiveRevision)),
            NormalizeTags(options.Tags),
            options.Events ?? ImmutableList<ServiceEvent>.Empty,
            Positive(options.Revision, nameof(options.Revision)),
            options.Lifecycle ?? throw new ArgumentNullException(nameof(options.Lifecycle)),
            DeclaredPlacement: null,
            PlanningFence: 0);
    }

    internal static ImmutableHashSet<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .ToImmutableHashSet(StringComparer.Ordinal);

    internal static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string RequiredObjectId(string value, string name)
    {
        if (!ObjectId.TryParse(value, out _))
            throw new ArgumentException("A MongoDB ObjectId is required.", name);
        return value.Trim();
    }

    private static string Required(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximumLength)
            throw new ArgumentException("A required value is invalid.", name);
        return value.Trim();
    }

    private static long Positive(long value, string name) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(name);
}
