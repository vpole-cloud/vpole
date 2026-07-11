using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Common;

public interface ISourceBuildDbContext
{
    IMongoCollection<SourceBuildConfiguration> SourceBuildConfigurations { get; }
    IMongoCollection<SourceBuildJob> SourceBuildJobs { get; }
}
