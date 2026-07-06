using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Users.Domain;

namespace Pole.Features.Users.Application.Common;

public interface IUserDbContext : IEventDbContext
{
    IMongoCollection<PoleUser> Users { get; }
}

