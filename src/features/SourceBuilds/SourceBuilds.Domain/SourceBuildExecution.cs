using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.SourceBuilds.Domain;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(AvailableSourceBuildExecution), typeof(ClaimedSourceBuildExecution))]
public closed record SourceBuildExecution;

public sealed record AvailableSourceBuildExecution(
    long Attempt
) : SourceBuildExecution;

public sealed record ClaimedSourceBuildExecution(
    string WorkerId,
    long Attempt,
    DateTime ExpiresAtUtc
) : SourceBuildExecution;
