using MongoDB.Bson.Serialization.Attributes;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.DomainNames;

public record DomainName(
    string Name,
    DomainNameStatus Status
) : BaseEntity;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(Used), typeof(Reserved))]
public abstract record DomainNameStatus;
public sealed record Used(string By) : DomainNameStatus;
public sealed record Reserved(string For) : DomainNameStatus;