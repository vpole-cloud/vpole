// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pole.AppHost.MongoDb;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class MongoReplicaSetCommandTests
{
    [Fact]
    public void Typed_commands_keep_the_replica_set_wire_shapes()
    {
        var registry = BsonSerializer.SerializerRegistry;
        var initiate
            = new ObjectCommand<ReplicaSetCommandResult>(
                new InitiateReplicaSetCommand(new ReplicaSetInitializationOptions()));
        var primary = new ObjectCommand<ReplicaSetPrimaryResult>(new ReadReplicaSetPrimaryCommand(1));

        Assert.Equal(new BsonDocument("replSetInitiate", new BsonDocument()), initiate.Render(registry).Document);
        Assert.Equal(new BsonDocument("isMaster", 1), primary.Render(registry).Document);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{ismaster:false,ok:1}", false)]
    [InlineData("{ismaster:true,ok:1,secondary:false}", true)]
    public void Primary_result_ignores_metadata_and_handles_missing_primary_state(string json, bool expected)
    {
        Assert.Equal(expected, BsonSerializer.Deserialize<ReplicaSetPrimaryResult>(json).IsPrimary);
        Assert.NotNull(BsonSerializer.Deserialize<ReplicaSetCommandResult>(json));
    }

    [Theory]
    [InlineData("{ismaster:1}")]
    [InlineData("{ismaster:'true'}")]
    [InlineData("{ismaster:null}")]
    public void Malformed_primary_state_is_not_coerced_to_a_boolean(string json) =>
        Assert.Throws<FormatException>(() => BsonSerializer.Deserialize<ReplicaSetPrimaryResult>(json));
}
