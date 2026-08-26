using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.WebServices.Application.Persistence;

/// <summary>
/// Expected provider states while a WebService consumer finalizes a resource deployment.
/// The enclosing operation decides whether to commit or abort its Mongo transaction.
/// </summary>
public union WebServiceFinalizationError(
    HostnameBindingCleanupError,
    ResourceDeploymentFinalizationConflict);
