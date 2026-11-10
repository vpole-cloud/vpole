using Pole.Features.Request;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Mediator;
using Xunit;

namespace Pole.Features.WebServices.Tests.Integration;

internal static class OutcomeAssertions
{
    internal static PaginatedResponse<WebServiceDto> AssertSuccess(this MediatorResult<GetWebServicesOutcome> result) =>
        Assert.IsType<PaginatedResponse<WebServiceDto>>(Assert.IsType<GetWebServicesOutcome>(result.Value).Value);

    internal static CreateWebServiceResponse AssertSuccess(this MediatorResult<CreateWebServiceOutcome> result) =>
        Assert.IsType<CreateWebServiceResponse>(Assert.IsType<CreateWebServiceOutcome>(result.Value).Value);

    internal static UpdateWebServiceResponse AssertSuccess(this MediatorResult<UpdateWebServiceOutcome> result) =>
        Assert.IsType<UpdateWebServiceResponse>(Assert.IsType<UpdateWebServiceOutcome>(result.Value).Value);

    internal static UpdateWebServiceOutcome AssertHandlerOutcome(this MediatorResult<UpdateWebServiceOutcome> result) =>
        Assert.IsType<UpdateWebServiceOutcome>(result.Value);

    internal static RollbackWebServiceResponse AssertSuccess(this MediatorResult<RollbackWebServiceOutcome> result) =>
        Assert.IsType<RollbackWebServiceResponse>(Assert.IsType<RollbackWebServiceOutcome>(result.Value).Value);

    internal static RollbackWebServiceError AssertFailure(this MediatorResult<RollbackWebServiceOutcome> result) =>
        Assert.IsType<RollbackWebServiceError>(Assert.IsType<RollbackWebServiceOutcome>(result.Value).Value);

    internal static WebServiceNotFound AssertFailure(this MediatorResult<GetWebServiceReleasesOutcome> result) =>
        Assert.IsType<WebServiceNotFound>(Assert.IsType<GetWebServiceReleasesOutcome>(result.Value).Value);
}
