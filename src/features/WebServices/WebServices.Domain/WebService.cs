using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Shared.Models;

namespace Pole.Features.WebServices.Domain;

public record WebService(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string Name,
    string ResourceGroupId,
    WebServiceSpec Spec,
    DnsRecordShortInfo DnsRecord,
    int ActiveRevision,
    ImmutableHashSet<string> Tags,
    ImmutableList<ServiceEvent> Events,
    long Revision,
    WebServiceLifecycle Lifecycle
)
{
    public WebService UpdateDesiredState(
        WebServiceSpec spec,
        ImmutableHashSet<string> tags)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(tags);

        return this with
        {
            Spec = spec,
            Tags = tags,
            ActiveRevision = checked(ActiveRevision + 1),
            Revision = checked(Revision + 1)
        };
    }

    public WebService Redeploy()
    {
        EnsureActive();
        return this with
        {
            ActiveRevision = checked(ActiveRevision + 1),
            Revision = checked(Revision + 1)
        };
    }

    public WebService BeginDeletion(DateTime requestedAtUtc)
    {
        if (Lifecycle is DeletingWebServiceLifecycle)
            return this;

        var normalizedRequestedAtUtc = requestedAtUtc.Kind switch
        {
            DateTimeKind.Utc => requestedAtUtc,
            DateTimeKind.Local => requestedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(requestedAtUtc, DateTimeKind.Utc)
        };
        return this with
        {
            Lifecycle = new DeletingWebServiceLifecycle(normalizedRequestedAtUtc),
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

public record WebServiceSpec(
    ImageInfo Image,
    int Replicas,
    WebServiceConfig Config,
    AutoscalingSpec? Autoscaling,
    ProbeSpec Probes
);

public record AutoscalingSpec(
    int MinReplicas,
    int MaxReplicas,
    AutoscalingMetric Metric
);

public record AutoscalingMetric(
    string Type,
    int TargetAverageUtilization
);

public record WebServiceConfig( 
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableList<PortMapping> PortMappings
);

/// <summary>
/// 
/// </summary>
/// <param name="ServicePort">Service Port</param>
/// <param name="TargetPort">Container Port</param>
/// <param name="Protocol">TCP, UDP, </param>
public record PortMapping(
    int ServicePort,
    string TargetPort,
    string Protocol,
    string Name
);

public record ImageInfo(
    string Server,
    string Name,
    string Tag = "latest",
    string? SecretName = null
)
{
    public string FullImageName => $"{Server}/{Name}:{Tag}";
}

public static class WebServiceFactory
{
    public sealed record CreateWebServiceOptions(
        string Id,
        int ActiveRevision,
        ImmutableHashSet<string> Tags,
        ImmutableList<ServiceEvent> Events,
        long Revision,
        WebServiceLifecycle Lifecycle
    );
    
    public static CreateWebServiceOptions DefaultOptions =>
        new CreateWebServiceOptions(
            Id: ObjectId.GenerateNewId().ToString(),
            ActiveRevision: 1,
            Tags: ImmutableHashSet<string>.Empty,
            Events: ImmutableList<ServiceEvent>.Empty,
            Revision: 1,
            Lifecycle: new ActiveWebServiceLifecycle()
        );
    
    public static WebService Create(
        string name,
        string tenantId,
        string resourceGroupId,
        WebServiceSpec spec,
        DnsRecordShortInfo dnsRecord,
        CreateWebServiceOptions options) =>
        new(
            options.Id,
            TenantId: tenantId,
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: spec,
            DnsRecord: dnsRecord,
            ActiveRevision: options.ActiveRevision,
            Tags: options.Tags,
            Events: options.Events,
            Revision: options.Revision,
            Lifecycle: options.Lifecycle
        );
}
