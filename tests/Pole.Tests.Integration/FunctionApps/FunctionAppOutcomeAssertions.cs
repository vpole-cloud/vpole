using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Quotas.Contracts;
using Pole.Mediator;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.FunctionApps;

internal static class FunctionAppOutcomeAssertions
{
    public static CreateFunctionAppResponse RequireSuccess(this MediatorResult<CreateFunctionAppOutcome> result) =>
        result.HandlerOutcome() switch
        {
            CreateFunctionAppResponse response => response,
            CreateFunctionAppError error => throw UnexpectedFailure(error),
        };

    public static UpdateFunctionAppResponse RequireSuccess(this MediatorResult<UpdateFunctionAppOutcome> result) =>
        result.HandlerOutcome() switch
        {
            UpdateFunctionAppResponse response => response,
            UpdateFunctionAppError error => throw UnexpectedFailure(error),
        };

    public static DeleteFunctionAppResponse RequireSuccess(this MediatorResult<DeleteFunctionAppOutcome> result) =>
        result.HandlerOutcome() switch
        {
            DeleteFunctionAppResponse response => response,
            DeleteFunctionAppError error => throw UnexpectedFailure(error),
        };

    public static RetryFunctionInvocationResponse RequireSuccess(
        this MediatorResult<RetryFunctionInvocationOutcome> result) => result.HandlerOutcome() switch
    {
        RetryFunctionInvocationResponse response => response,
        RetryFunctionInvocationError error => throw UnexpectedFailure(error),
    };

    public static IReadOnlyList<FunctionInvocationDto> RequireSuccess(
        this MediatorResult<GetFunctionInvocationsOutcome> result) => result.HandlerOutcome() switch
    {
        IReadOnlyList<FunctionInvocationDto> response => response,
        FunctionAppNotFound error => throw UnexpectedFailure(error),
    };

    public static FunctionUsageResponse RequireSuccess(this MediatorResult<GetFunctionUsageOutcome> result) =>
        result.HandlerOutcome() switch
        {
            FunctionUsageResponse response => response,
            FunctionAppNotFound error => throw UnexpectedFailure(error),
        };

    public static FunctionRevisionLifecycleCompleted RequireLifecycleSuccess(
        this FunctionRevisionLifecycleOutcome outcome) =>
        Assert.IsType<FunctionRevisionLifecycleCompleted>(outcome.LifecycleValue());

    public static object LifecycleValue(this FunctionRevisionLifecycleOutcome outcome) => outcome switch
    {
        FunctionRevisionLifecycleCompleted value => value,
        FunctionAppNotFound value => value,
        FunctionAppIsNotEnabled value => value,
        FunctionRevisionNotFound value => value,
        FunctionRevisionNotProvisioning value => value,
        FunctionRevisionNotReady value => value,
        FunctionRevisionNotDraining value => value,
        FunctionRevisionNotPending value => value,
        FunctionRevisionConcurrencyConflict value => value,
        FunctionAppConcurrencyConflict value => value,
        FunctionRouteBindingUnavailable value => value,
        FunctionRevisionHasDurableInvocations value => value,
        QuotaExecutionError value => value,
    };

    public static TOutcome HandlerOutcome<TOutcome>(this MediatorResult<TOutcome> result)
        where TOutcome : notnull => result.Match(
        outcome => outcome,
        rejected => throw new XunitException(
            $"Expected a Function App handler outcome, but the pipeline returned " +
            $"'{rejected.Reason.GetType().Name}'."));

    private static XunitException UnexpectedFailure(object error) => new(
        $"Expected a successful Function App outcome, but received '{error.GetType().Name}'.");
}
