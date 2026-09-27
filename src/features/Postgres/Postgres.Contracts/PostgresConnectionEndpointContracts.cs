using System.Collections.Immutable;
using MongoDB.Driver;

namespace Pole.Features.Postgres.Contracts;

public sealed record PostgresConnectionEndpointDto(
    string Id,
    int ServicePort,
    int TargetPort);

public sealed record PostgresConnectionEndpointsFound(
    string ResourceId,
    string ServiceName,
    ImmutableList<PostgresConnectionEndpointDto> Endpoints);

public sealed record PostgresConnectionResourceMissing;

public sealed record PostgresConnectionResourceDeleting;

public union LoadPostgresConnectionEndpointsOutcome(
    PostgresConnectionEndpointsFound,
    PostgresConnectionResourceMissing,
    PostgresConnectionResourceDeleting);

public delegate Task<LoadPostgresConnectionEndpointsOutcome> LoadPostgresConnectionEndpoints(
    string tenantId,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
