using System.Collections.Immutable;
using MongoDB.Bson;
using Pole.Features.Quotas.Domain;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

public sealed class QuotaPrimitiveBsonCompatibilityTests
{
    private static readonly DateTime NowUtc =
        new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PersistedQuotaValues_KeepTheirNestedBsonShape()
    {
        var key = new QuotaKey("CpuMillis");
        var usage = QuotaDomainFactory.CreateUsage(
            "507f1f77bcf86cd799439011",
            NowUtc) with
        {
            Values = ImmutableList.Create(new QuotaValue(key, 250))
        };

        var document = usage.ToBsonDocument();
        var value = document["Values"].AsBsonArray[0].AsBsonDocument;

        Assert.Equal("CpuMillis", value["Key"].AsBsonDocument["Value"].AsString);
        Assert.Equal(250, value["Value"].AsInt64);
    }

    [Fact]
    public void PersistedReservation_RoundTripsItsResourceKeyAsAString()
    {
        var reservation = QuotaDomainFactory.CreateReservation(
            "507f1f77bcf86cd799439011",
            QuotaDelta.From(new QuotaKey("MemoryMi"), 512),
            QuotaResourceKey.Create("redis-service", "507f1f77bcf86cd799439012"),
            NowUtc,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromDays(1));

        var document = reservation.ToBsonDocument();

        Assert.Equal(
            "redis-service:507f1f77bcf86cd799439012",
            document["ResourceKey"].AsString);
        Assert.Equal(
            "MemoryMi",
            document["Delta"].AsBsonArray[0].AsBsonDocument["Key"]
                .AsBsonDocument["Value"].AsString);
        Assert.Equal(
            512,
            document["Delta"].AsBsonArray[0].AsBsonDocument["Value"].AsInt64);

        var roundTripped = MongoDB.Bson.Serialization.BsonSerializer
            .Deserialize<QuotaReservation>(document);
        Assert.Equal(
            "redis-service:507f1f77bcf86cd799439012",
            roundTripped.ResourceKey.Value);
    }
}
