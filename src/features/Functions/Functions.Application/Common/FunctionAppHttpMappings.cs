using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Revisions;

namespace Pole.Features.Functions.Application.Common;

public static class FunctionAppHttpMappings
{
    public static HttpProblem MapSecretBindingRemovalRequired(FunctionSecretBindingRemovalRequired error) => Conflict(
        "function-apps.secret-bindings-removal-required",
        "Remove Git-owned Secret bindings before deleting the Function App", error.FunctionAppId);

    public static HttpProblem MapGitOwned(FunctionAppGitOwnedConfiguration error) => Conflict(
        "function-apps.git-owned", "Function App configuration is managed by GitOps", error.FunctionAppId);

    public static HttpProblem MapConfigurationUnavailable(FunctionAppConfigurationUnavailable error) => Conflict(
        "function-apps.configuration-unavailable",
        "The latest accepted Function configuration is unavailable",
        error.FunctionAppId);

    public static HttpProblem MapSecretReferenceRejected(FunctionSecretReferenceRejected _) => new(
        (int)HttpStatusCode.Conflict,
        PoleProblemTypeId.FromCode("function-apps.secret-reference-unavailable"),
        "Function Secret references are not available or authorized",
        "function-apps.secret-reference-unavailable");

    public static HttpProblem MapNotFound(FunctionAppNotFound error) =>
        new(
            (int)HttpStatusCode.NotFound,
            PoleProblemTypeId.FromCode("function-apps.not-found"),
            "Function App not found",
            "function-apps.not-found",
            extensions: new Dictionary<string, object>
            {
                ["functionAppId"] = error.FunctionAppId
            });

    public static HttpProblem MapDefaultDeploymentTargetUnavailable(
        FunctionAppDefaultDeploymentTargetUnavailable _) =>
        new(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode("function-apps.default-deployment-target.unavailable"),
            "Default deployment target is unavailable",
            "function-apps.default-deployment-target.unavailable");

    public static HttpProblem MapPendingRevision(FunctionAppHasPendingRevision error) =>
        Conflict(
            "function-apps.pending-revision.exists",
            "Function App already has a pending revision",
            error.FunctionAppId);

    public static HttpProblem MapNotEnabled(FunctionAppIsNotEnabled error) =>
        Conflict(
            "function-apps.not-enabled",
            "Function App is not enabled",
            error.FunctionAppId);

    public static HttpProblem MapResourceDeploymentMissing(
        FunctionAppResourceDeploymentMissing error) =>
        Conflict(
            "function-apps.resource-deployment.missing",
            "Function App resource deployment is unavailable",
            error.FunctionAppId);

    public static HttpProblem MapRevisionChanged(FunctionAppRevisionChanged error) =>
        Conflict(
            "function-apps.revision-changed",
            "Function App revision changed concurrently",
            error.FunctionAppId);

    public static HttpProblem MapDeletionChanged(FunctionAppDeletionChanged error) =>
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

    public static HttpProblem MapRetryError(RetryFunctionInvocationError error) => error switch
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
