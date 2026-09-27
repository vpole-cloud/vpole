using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
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
    WebServiceLifecycle Lifecycle)
{
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

        var normalizedTags = WebServiceFactory.NormalizeTags(tags);
        return Equals(Spec, spec) && Tags.SetEquals(normalizedTags) && Equals(Exposure, exposure)
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
        EnsureActive();
        return this with
        {
            ActiveRevision = checked(ActiveRevision + 1),
            Revision = checked(Revision + 1)
        };
    }

    public WebService Redeploy() => AdvancePlacementDesiredState();

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

    private void EnsureActive()
    {
        if (Lifecycle is not ActiveWebServiceLifecycle)
            throw new InvalidOperationException("A deleting web service cannot be updated.");
    }
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ActiveWebServiceLifecycle), typeof(DeletingWebServiceLifecycle))]
public abstract record WebServiceLifecycle;

public sealed record ActiveWebServiceLifecycle : WebServiceLifecycle;

public sealed record DeletingWebServiceLifecycle(DateTime RequestedAtUtc) : WebServiceLifecycle;

/// <summary>Configuration shared by every physical deployment of a logical web service.</summary>
public sealed record WebServiceSpec(
    ImageInfo Image,
    WebServiceConfig Config,
    AutoscalingSpec? Autoscaling,
    ProbeSpec Probes);

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
public sealed record WebServiceConfig(
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableList<PortMapping> PortMappings);
public sealed record PortMapping(
    int ServicePort,
    string TargetPort,
    string Protocol,
    string Name);
public sealed record ImageInfo(
    string Server,
    string Name,
    string Tag = "latest",
    string? SecretName = null)
{
    public string FullImageName => $"{Server}/{Name}:{Tag}";
}

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
            options.Lifecycle ?? throw new ArgumentNullException(nameof(options.Lifecycle)));
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
