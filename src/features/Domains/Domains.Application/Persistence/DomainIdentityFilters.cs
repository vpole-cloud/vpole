using MongoDB.Driver;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Persistence;

internal static class DomainIdentityFilters
{
    // MongoDB's scalar discriminator identifies the persisted identity variant.
    public const string Discriminator = "Identity._t";

    public static FilterDefinition<T> Generated<T>() =>
        Builders<T>.Filter.Eq(Discriminator, nameof(GeneratedDomainIdentity));

    public static FilterDefinition<T> Named<T>() =>
        Builders<T>.Filter.Eq(Discriminator, nameof(NamedDomainIdentity));
}
