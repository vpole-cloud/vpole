using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Common;

public interface ISourceBuildDbContext : IEventDbContext
{
    IMongoCollection<SourceBuildConfiguration> SourceBuildConfigurations { get; }
    IMongoCollection<SourceBuildJob> SourceBuildJobs { get; }
}
