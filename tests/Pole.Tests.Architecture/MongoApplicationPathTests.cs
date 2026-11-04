using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class MongoApplicationPathTests
{
    [Theory]
    [InlineData("Feature.Application/Mongo/Filters/ItemFilters", MongoApplicationPath.Mongo)]
    [InlineData("Feature.Application/Runs/Mongo/RunStore", MongoApplicationPath.Mongo)]
    [InlineData(@"src\Feature.Application\Sources\Mongo\SourceStore", MongoApplicationPath.Mongo)]
    [InlineData("Feature.Application/Persistence/Store", MongoApplicationPath.Mongo)]
    [InlineData("Feature.Application/Runs/RunStore", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Endpoints/Handler", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Endpoints/Mongo/Handler", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Runs/MongoQueries/Store", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Runs/Helpers/Mongo/Store", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Runs/Persistence/Store", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Mongo/Migrations/Index", MongoApplicationPath.Application)]
    [InlineData("Feature.Migrations/Mongo/Index", MongoApplicationPath.OutsideApplication)]
    [InlineData("Feature.Application.Tests/Mongo/Store", MongoApplicationPath.OutsideApplication)]
    public void CSharp_and_FSharp_share_the_same_mongo_boundary(string path, MongoApplicationPath expected)
    {
        Assert.Equal(expected, MongoApplicationPaths.Classify($"{path}.cs"));
        Assert.Equal(expected, MongoApplicationPaths.Classify($"{path}.fs"));
    }
}
