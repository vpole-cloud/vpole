namespace Pole.Features.WebServices.Contracts;

public delegate Task<bool> WebServiceExists(
    string tenantId,
    string webServiceId,
    CancellationToken cancellationToken = default);
