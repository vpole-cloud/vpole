using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Request;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Application.Common;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Deployments;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Deployments;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Mediator;
using Xunit.Sdk;

namespace Pole.Tests.Integration.Infrastructure;

public static class ResultAssertionExtensions
{
    public static ResourceGroupDto AssertSuccess(
        this MediatorResult<CreateResourceGroupOutcome> result) =>
        AssertSuccess<CreateResourceGroupOutcome>(result) switch
        {
            ResourceGroupDto success => success,
            Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups.ResourceGroupNameAlreadyExists error
                => UnexpectedError<ResourceGroupDto>(error)
        };

    public static PaginatedResponse<WebServiceDto> AssertSuccess(
        this MediatorResult<GetWebServicesOutcome> result) =>
        AssertSuccess<GetWebServicesOutcome>(result) switch
        {
            PaginatedResponse<WebServiceDto> success => success
        };

    public static PaginatedResponse<FunctionAppDto> AssertSuccess(
        this MediatorResult<GetFunctionAppsOutcome> result) =>
        AssertSuccess<GetFunctionAppsOutcome>(result) switch
        {
            PaginatedResponse<FunctionAppDto> success => success
        };

    public static WebServiceDeploymentCompletionRecorded AssertSuccess(
        this CompleteWebServiceDeploymentOutcome outcome) => outcome switch
        {
            WebServiceDeploymentCompletionRecorded success => success,
            WebServiceFinalizationError error =>
                UnexpectedError<WebServiceDeploymentCompletionRecorded>(error)
        };

    public static WebServiceDeletionFinalized AssertSuccess(
        this FinalizeWebServiceDeletionOutcome outcome) => outcome switch
        {
            WebServiceDeletionFinalized success => success,
            WebServiceFinalizationError error =>
                UnexpectedError<WebServiceDeletionFinalized>(error)
        };


    public static CreateWebServiceResponse AssertSuccess(
        this MediatorResult<CreateWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<CreateWebServiceOutcome>(result).AssertSuccess();

    public static CreateWebServiceError AssertFailure(
        this MediatorResult<CreateWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<CreateWebServiceOutcome>(result).AssertFailure();

    public static UpdateWebServiceResponse AssertSuccess(
        this MediatorResult<UpdateWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<UpdateWebServiceOutcome>(result).AssertSuccess();

    public static RollbackWebServiceResponse AssertSuccess(
        this MediatorResult<RollbackWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<RollbackWebServiceOutcome>(result).AssertSuccess();

    public static RollbackWebServiceError AssertFailure(
        this MediatorResult<RollbackWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<RollbackWebServiceOutcome>(result).AssertFailure();

    public static PlacementSummaryDto AssertSuccess(
        this MediatorResult<DeleteWebServiceDeploymentOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<DeleteWebServiceDeploymentOutcome>(result).AssertSuccess();

    public static DeleteWebServiceDeploymentError AssertFailure(
        this MediatorResult<DeleteWebServiceDeploymentOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<DeleteWebServiceDeploymentOutcome>(result).AssertFailure();

    public static PlacementSummaryDto AssertSuccess(
        this MediatorResult<RetryWebServiceDeploymentOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<RetryWebServiceDeploymentOutcome>(result).AssertSuccess();

    public static PlacementSummaryDto AssertSuccess(
        this MediatorResult<UpdateWebServiceDeploymentOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<UpdateWebServiceDeploymentOutcome>(result).AssertSuccess();

    public static WebServiceReleaseHistoryDto AssertSuccess(
        this MediatorResult<GetWebServiceReleasesOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<GetWebServiceReleasesOutcome>(result).AssertSuccess();

    public static WebServiceNotFound AssertFailure(
        this MediatorResult<GetWebServiceReleasesOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<GetWebServiceReleasesOutcome>(result).AssertFailure();

    public static DeleteWebServiceResponse AssertSuccess(
        this MediatorResult<DeleteWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<DeleteWebServiceOutcome>(result).AssertSuccess();

    public static CreateWebServiceResponse AssertSuccess(this CreateWebServiceOutcome outcome) =>
        outcome switch
        {
            CreateWebServiceResponse success => success,
            CreateWebServiceError error => UnexpectedError<CreateWebServiceResponse>(error)
        };

    public static CreateWebServiceError AssertFailure(this CreateWebServiceOutcome outcome) =>
        outcome switch
        {
            CreateWebServiceResponse success => UnexpectedSuccess<CreateWebServiceError>(success),
            CreateWebServiceError error => error
        };

    public static UpdateWebServiceResponse AssertSuccess(this UpdateWebServiceOutcome outcome) =>
        outcome switch
        {
            UpdateWebServiceResponse success => success,
            UpdateWebServiceError error => UnexpectedError<UpdateWebServiceResponse>(error)
        };

    public static RollbackWebServiceResponse AssertSuccess(this RollbackWebServiceOutcome outcome) =>
        outcome switch
        {
            RollbackWebServiceResponse success => success,
            RollbackWebServicePublicSpecIncompatible incompatible =>
                UnexpectedError<RollbackWebServiceResponse>(incompatible),
            RollbackWebServiceError error => UnexpectedError<RollbackWebServiceResponse>(error)
        };

    public static RollbackWebServiceError AssertFailure(this RollbackWebServiceOutcome outcome) =>
        outcome switch
        {
            RollbackWebServiceResponse success => UnexpectedSuccess<RollbackWebServiceError>(success),
            RollbackWebServicePublicSpecIncompatible incompatible =>
                UnexpectedError<RollbackWebServiceError>(incompatible),
            RollbackWebServiceError error => error
        };

    public static PlacementSummaryDto AssertSuccess(
        this DeleteWebServiceDeploymentOutcome outcome) => outcome switch
        {
            PlacementSummaryDto success => success,
            DeleteWebServiceDeploymentError error => UnexpectedError<PlacementSummaryDto>(error)
        };

    public static DeleteWebServiceDeploymentError AssertFailure(
        this DeleteWebServiceDeploymentOutcome outcome) => outcome switch
        {
            PlacementSummaryDto success =>
                UnexpectedSuccess<DeleteWebServiceDeploymentError>(success),
            DeleteWebServiceDeploymentError error => error
        };

    public static PlacementSummaryDto AssertSuccess(
        this RetryWebServiceDeploymentOutcome outcome) => outcome switch
        {
            PlacementSummaryDto success => success,
            RetryWebServiceDeploymentError error => UnexpectedError<PlacementSummaryDto>(error)
        };

    public static PlacementSummaryDto AssertSuccess(
        this UpdateWebServiceDeploymentOutcome outcome) => outcome switch
        {
            PlacementSummaryDto success => success,
            UpdateWebServiceDeploymentError error => UnexpectedError<PlacementSummaryDto>(error)
        };

    public static WebServiceReleaseHistoryDto AssertSuccess(
        this GetWebServiceReleasesOutcome outcome) => outcome switch
        {
            WebServiceReleaseHistoryDto success => success,
            WebServiceNotFound error => UnexpectedError<WebServiceReleaseHistoryDto>(error)
        };

    public static WebServiceNotFound AssertFailure(
        this GetWebServiceReleasesOutcome outcome) => outcome switch
        {
            WebServiceReleaseHistoryDto success => UnexpectedSuccess<WebServiceNotFound>(success),
            WebServiceNotFound error => error
        };

    public static DeleteWebServiceResponse AssertSuccess(this DeleteWebServiceOutcome outcome) =>
        outcome switch
        {
            DeleteWebServiceResponse success => success,
            DeleteWebServiceError error => UnexpectedError<DeleteWebServiceResponse>(error)
        };

    private static TSuccess UnexpectedError<TSuccess>(object error)
        where TSuccess : notnull =>
        throw new XunitException(
            $"Expected a successful result, but received {error.GetType().Name}: {error}");

    private static TError UnexpectedSuccess<TError>(object success)
        where TError : notnull =>
        throw new XunitException(
            $"Expected a failed result, but received {success.GetType().Name}.");

    public static T AssertSuccess<T>(this MediatorResult<T> result)
        where T : notnull
        => result.Match(
            outcome => outcome,
            rejected => throw new XunitException(
                $"Expected a handler outcome, but the pipeline returned " +
                $"'{rejected.Reason.GetType().Name}'."));

    public static T AssertHandlerOutcome<T>(this MediatorResult<T> result)
        where T : notnull =>
        result.Match(
            outcome => outcome,
            rejected => throw new XunitException(
                $"Expected a handler outcome, but the pipeline returned " +
                $"'{rejected.Reason.GetType().Name}'."));

    public static RequestValidationRejected AssertValidationRejected<T>(
        this MediatorResult<T> result)
        where T : notnull =>
        result.Match(
            _ => throw new XunitException(
                "Expected request validation to reject the pipeline, but the handler returned an outcome."),
            rejected => rejected.Reason switch
            {
                RequestValidationRejected validation => validation,
                var reason => throw new XunitException(
                    "Expected a request validation rejection, but the pipeline returned " +
                    $"'{reason.GetType().Name}'.")
            });
}
