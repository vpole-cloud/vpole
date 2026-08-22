using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Application.Persistence.Mutations;

namespace Pole.Features.Functions.Application.Common;

public static class FunctionAppHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<FunctionAppNotFound>(MapNotFound),
            HttpProblemMapping.For<FunctionAppResourceGroupNotFound>(MapResourceGroupNotFound),
            HttpProblemMapping.For<FunctionAppResourceGroupDeleting>(MapResourceGroupDeleting),
            HttpProblemMapping.For<FunctionAppDefaultDeploymentTargetUnavailable>(
                MapDefaultDeploymentTargetUnavailable),
            HttpProblemMapping.For<FunctionAppHasPendingRevision>(MapPendingRevision),
            HttpProblemMapping.For<FunctionAppIsNotEnabled>(MapNotEnabled),
            HttpProblemMapping.For<FunctionAppRevisionChanged>(MapRevisionChanged),
            HttpProblemMapping.For<FunctionAppDeletionChanged>(MapDeletionChanged),
            HttpProblemMapping.For<RetryFunctionInvocationError>(MapRetryError)
        ]);

    private static HttpProblem MapNotFound(FunctionAppNotFound error) =>
        new(
            (int)HttpStatusCode.NotFound,
            PoleProblemTypeId.FromCode("function-apps.not-found"),
            "Function App not found",
            "function-apps.not-found",
            extensions: new Dictionary<string, object>
            {
                ["functionAppId"] = error.FunctionAppId
            });

    private static HttpProblem MapResourceGroupNotFound(
        FunctionAppResourceGroupNotFound error) =>
        new(
            (int)HttpStatusCode.NotFound,
            PoleProblemTypeId.FromCode("function-apps.resource-group.not-found"),
            "Resource group not found",
            "function-apps.resource-group.not-found",
            extensions: new Dictionary<string, object>
            {
                ["resourceGroupId"] = error.ResourceGroupId
            });

    private static HttpProblem MapResourceGroupDeleting(
        FunctionAppResourceGroupDeleting error) =>
        new(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode("function-apps.resource-group.deleting"),
            "Resource group is being deleted",
            "function-apps.resource-group.deleting",
            extensions: new Dictionary<string, object>
            {
                ["resourceGroupId"] = error.ResourceGroupId
            });

    private static HttpProblem MapDefaultDeploymentTargetUnavailable(
        FunctionAppDefaultDeploymentTargetUnavailable _) =>
        new(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode("function-apps.default-deployment-target.unavailable"),
            "Default deployment target is unavailable",
            "function-apps.default-deployment-target.unavailable");

    private static HttpProblem MapPendingRevision(FunctionAppHasPendingRevision error) =>
        Conflict(
            "function-apps.pending-revision.exists",
            "Function App already has a pending revision",
            error.FunctionAppId);

    private static HttpProblem MapNotEnabled(FunctionAppIsNotEnabled error) =>
        Conflict(
            "function-apps.not-enabled",
            "Function App is not enabled",
            error.FunctionAppId);

    private static HttpProblem MapRevisionChanged(FunctionAppRevisionChanged error) =>
        Conflict(
            "function-apps.revision-changed",
            "Function App revision changed concurrently",
            error.FunctionAppId);

    private static HttpProblem MapDeletionChanged(FunctionAppDeletionChanged error) =>
        Conflict(
            "function-apps.deletion-changed",
            "Function App state changed concurrently",
            error.FunctionAppId);

    private static HttpProblem Conflict(string code, string title, string functionAppId) =>
        new(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: new Dictionary<string, object>
            {
                ["functionAppId"] = functionAppId
            });

    private static HttpProblem MapRetryError(RetryFunctionInvocationError error) => error switch
    {
        FunctionAppNotFound notFound => MapNotFound(notFound),
        FunctionInvocationNotFound notFound => new(
            (int)HttpStatusCode.NotFound,
            PoleProblemTypeId.FromCode("function-apps.invocation.not-found"),
            "Function invocation not found",
            "function-apps.invocation.not-found",
            extensions: new Dictionary<string, object>
            {
                ["invocationId"] = notFound.InvocationId
            }),
        FunctionInvocationNotRetryable rejected => InvocationConflict(
            "function-apps.invocation.not-retryable",
            "Only a failed Function invocation can be retried",
            rejected.InvocationId),
        FunctionInvocationRevisionNoLongerActive rejected => InvocationConflict(
            "function-apps.invocation.revision-not-active",
            "Only an invocation from the active Function revision can be retried",
            rejected.InvocationId),
        FunctionInvocationTimerNoLongerActive rejected => InvocationConflict(
            "function-apps.invocation.timer-not-active",
            "The invocation timer is no longer active",
            rejected.InvocationId),
        FunctionInvocationConcurrencyForbidden rejected => InvocationConflict(
            "function-apps.invocation.concurrency-forbidden",
            "The timer concurrency policy forbids overlapping invocations",
            rejected.InvocationId),
        FunctionInvocationTimerChanged rejected => InvocationConflict(
            "function-apps.invocation.timer-changed",
            "The timer state changed concurrently",
            rejected.InvocationId),
        FunctionInvocationAppChanged rejected => Conflict(
            "function-apps.invocation.app-changed",
            "The Function App state changed concurrently",
            rejected.FunctionAppId),
        FunctionInvocationChanged rejected => InvocationConflict(
            "function-apps.invocation.changed",
            "Function invocation state changed concurrently",
            rejected.InvocationId)
    };

    private static HttpProblem InvocationConflict(
        string code,
        string title,
        string invocationId) =>
        new(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: new Dictionary<string, object>
            {
                ["invocationId"] = invocationId
            });
}
