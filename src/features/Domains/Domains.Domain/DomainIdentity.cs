using MongoDB.Bson.Serialization.Attributes;
using Pole.Workloads;

namespace Pole.Features.Domains.Domain;

[BsonKnownTypes(typeof(GeneratedDomainIdentity), typeof(NamedDomainIdentity))]
public closed record DomainIdentity;

public sealed record GeneratedDomainIdentity : DomainIdentity;

public sealed record NamedDomainIdentity(
    string TechnicalName
) : DomainIdentity;

public static class DomainIdentityFactory
{
    public static DomainIdentity Generated() => new GeneratedDomainIdentity();

    public static DomainIdentity Named(string technicalName) =>
        new NamedDomainIdentity(ResourceTechnicalName.Require(technicalName));
}
