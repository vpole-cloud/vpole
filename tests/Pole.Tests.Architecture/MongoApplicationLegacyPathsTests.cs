using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class MongoApplicationLegacyPathsTests
{
    [Fact]
    public void Temporary_allowances_cover_exactly_nine_existing_source_build_implementations()
    {
        Assert.Equal(9, MongoApplicationLegacyPaths.Files.Count);
        foreach (var file in MongoApplicationLegacyPaths.Files)
        {
            Assert.True(File.Exists(Path.Combine(ArchitectureSources.RepositoryRoot, file)), file);
            Assert.Contains("IMongoCollection",
                File.ReadAllText(Path.Combine(ArchitectureSources.RepositoryRoot, file)));
            Assert.Equal(MongoApplicationPath.Application, MongoApplicationPaths.Classify(file));
            Assert.Equal(MongoApplicationPath.Application,
                MongoApplicationPaths.Classify(Path.Combine(ArchitectureSources.RepositoryRoot, file)));
            Assert.Equal(MongoApplicationPath.Application, MongoApplicationPaths.Classify(file.Replace('/', '\\')));
        }
    }

    [Theory]
    [InlineData("NewJob.cs")]
    [InlineData("ClaimQueuedSourceBuildJob.fs")]
    [InlineData("ClaimQueuedSourceBuildJob.Generated.cs")]
    [InlineData("Nested/ClaimQueuedSourceBuildJob.cs")]
    [InlineData("Consumers/ClaimQueuedSourceBuildJob.cs")]
    public void New_or_relocated_files_do_not_inherit_the_allowance(string name)
    {
        var file = $"src/features/SourceBuilds/SourceBuilds.Application/Persistence/Jobs/{name}";
        Assert.Equal(MongoApplicationPath.EntryPoint, MongoApplicationPaths.Classify(file));
    }

    [Fact]
    public void Another_feature_cannot_reuse_an_allowed_file_name()
    {
        const string file = "src/features/Other/Other.Application/Persistence/Jobs/ClaimQueuedSourceBuildJob.cs";
        Assert.Equal(MongoApplicationPath.EntryPoint, MongoApplicationPaths.Classify(file));
    }
}
