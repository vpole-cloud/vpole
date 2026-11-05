using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Mongo;

namespace Pole.Features.WebServices.Application.Lifecycle;

/// <summary>
/// Expected provider states while a WebService consumer finalizes a resource deployment.
/// The enclosing operation decides whether to commit or abort its Mongo transaction.
/// </summary>
public union WebServiceFinalizationError(
    HostnameBindingCleanupError,
    ResourceDeploymentFinalizationConflict,
    ResourceRegistrationMutationError);
