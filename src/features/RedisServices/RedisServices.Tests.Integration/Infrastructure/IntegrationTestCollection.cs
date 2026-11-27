// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<MongoReplicaSetFixture>
{
    public const string Name = "redis-integration-tests";
}
