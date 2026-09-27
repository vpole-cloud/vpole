using System.Collections.Immutable;
using MongoDB.Driver;

namespace Pole.Features.WebServices.Contracts;

public sealed record WebServiceConnectionEndpointDto(
    string Id,
    int ServicePort,
    int TargetPort);

public sealed record WebServiceConnectionEndpointsFound(
    string ResourceId,
    string ServiceName,
    ImmutableList<WebServiceConnectionEndpointDto> Endpoints);

public sealed record WebServiceConnectionResourceMissing;

public sealed record WebServiceConnectionResourceDeleting;

public union LoadWebServiceConnectionEndpointsOutcome(
    WebServiceConnectionEndpointsFound,
    WebServiceConnectionResourceMissing,
    WebServiceConnectionResourceDeleting);

public delegate Task<LoadWebServiceConnectionEndpointsOutcome> LoadWebServiceConnectionEndpoints(
    string tenantId,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
