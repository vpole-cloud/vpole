// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class MongoApplicationPathTests
{
    [Theory]
    [InlineData("Feature.Application/FeatureFilters", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Runs/RunStore", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Persistence/Store", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Runs/Mongo/RunStore", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Endpoints/Handler", MongoApplicationPath.EntryPoint)]
    [InlineData("Feature.Application/Consumers/Consumer", MongoApplicationPath.EntryPoint)]
    [InlineData("Feature.Application/Jobs/Job", MongoApplicationPath.EntryPoint)]
    [InlineData("Feature.Application/Runs/Jobs/Job", MongoApplicationPath.EntryPoint)]
    [InlineData("Feature.Application/Endpoints/Mongo/Handler", MongoApplicationPath.EntryPoint)]
    [InlineData("Feature.Application/Inventory/Consumers/Consumer", MongoApplicationPath.EntryPoint)]
    [InlineData(@"src\Feature.Application\Runs\Jobs\Job", MongoApplicationPath.EntryPoint)]
    [InlineData("Feature.Application/JobScheduling/Store", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/ConsumerModels/Store", MongoApplicationPath.Application)]
    [InlineData("Feature.Application/Migrations/Index", MongoApplicationPath.Application)]
    [InlineData("Feature.Migrations/Jobs/Index", MongoApplicationPath.OutsideApplication)]
    [InlineData("Feature.Application.Tests/Endpoints/Store", MongoApplicationPath.OutsideApplication)]
    public void CSharp_and_FSharp_share_the_same_entry_point_boundary(string path, MongoApplicationPath expected)
    {
        Assert.Equal(expected, MongoApplicationPaths.Classify($"{path}.cs"));
        Assert.Equal(expected, MongoApplicationPaths.Classify($"{path}.fs"));
    }
}
