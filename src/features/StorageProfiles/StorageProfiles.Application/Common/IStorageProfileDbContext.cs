using MongoDB.Driver;
using Pole.Database;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Common;

public interface IStorageProfileDbContext : IMongoDbContext
{
    IMongoCollection<StorageProfile> StorageProfiles { get; }
}
