using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Common;

public interface IFunctionAppDbContext : IEventDbContext
{
    IMongoCollection<FunctionApp> FunctionApps { get; }
    IMongoCollection<FunctionRevision> FunctionRevisions { get; }
    IMongoCollection<FunctionRouteBinding> FunctionRouteBindings { get; }
}
