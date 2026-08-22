using Pole.Features.Quotas.Contracts;

namespace Pole.Features.Functions.Application.Common;

public sealed record FunctionRevisionLifecycleCompleted;

public sealed record FunctionRevisionNotFound(string FunctionAppId, string RevisionId);

public sealed record FunctionRevisionNotProvisioning(string RevisionId);

public sealed record FunctionRevisionNotReady(string RevisionId);

public sealed record FunctionRevisionNotDraining(string RevisionId);

public sealed record FunctionRevisionNotPending(string FunctionAppId, string RevisionId);

public sealed record FunctionRevisionConcurrencyConflict(string FunctionAppId, string RevisionId);

public sealed record FunctionAppConcurrencyConflict(string FunctionAppId);

public sealed record FunctionRouteBindingUnavailable(string FunctionAppId);

public sealed record FunctionRevisionHasDurableInvocations(string FunctionAppId, string RevisionId);

public union FunctionRevisionLifecycleOutcome(
    FunctionRevisionLifecycleCompleted,
    FunctionAppNotFound,
    FunctionAppIsNotEnabled,
    FunctionRevisionNotFound,
    FunctionRevisionNotProvisioning,
    FunctionRevisionNotReady,
    FunctionRevisionNotDraining,
    FunctionRevisionNotPending,
    FunctionRevisionConcurrencyConflict,
    FunctionAppConcurrencyConflict,
    FunctionRouteBindingUnavailable,
    FunctionRevisionHasDurableInvocations,
    QuotaExecutionError);

public static class FunctionRevisionLifecycleOutcomeExtensions
{
    public static string FailureMessage(this FunctionRevisionLifecycleOutcome outcome) => outcome switch
    {
        FunctionRevisionLifecycleCompleted => throw new InvalidOperationException(
            "A successful Function revision lifecycle outcome has no failure message."),
        FunctionAppNotFound => "Function App not found.",
        FunctionAppIsNotEnabled => "Function App is not enabled.",
        FunctionRevisionNotFound => "Function revision not found.",
        FunctionRevisionNotProvisioning =>
            "Only a provisioning Function revision can become ready.",
        FunctionRevisionNotReady => "Function revision is not ready for promotion.",
        FunctionRevisionNotDraining => "Only a draining Function revision can be retired.",
        FunctionRevisionNotPending => "Function revision is not pending for this app.",
        FunctionRevisionConcurrencyConflict => "Function revision state changed concurrently.",
        FunctionAppConcurrencyConflict => "Function App state changed concurrently.",
        FunctionRouteBindingUnavailable => "Function route binding is unavailable.",
        FunctionRevisionHasDurableInvocations =>
            "The Function revision still has durable invocations to process.",
        QuotaExecutionError => "Function revision quota update failed."
    };
}
