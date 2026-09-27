using System.Collections.Immutable;
using MongoDB.Driver;

namespace Pole.Features.RedisServices.Contracts;

public sealed record RedisConnectionEndpointDto(
    string Id,
    int ServicePort,
    int TargetPort);

public sealed record RedisConnectionEndpointsFound(
    string ResourceId,
    string ServiceName,
    ImmutableList<RedisConnectionEndpointDto> Endpoints);

public sealed record RedisConnectionResourceMissing;

public sealed record RedisConnectionResourceDeleting;

public union LoadRedisConnectionEndpointsOutcome(
    RedisConnectionEndpointsFound,
    RedisConnectionResourceMissing,
    RedisConnectionResourceDeleting);

public delegate Task<LoadRedisConnectionEndpointsOutcome> LoadRedisConnectionEndpoints(
    string tenantId,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
