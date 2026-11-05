using System.Collections.Immutable;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Common;

internal static class FunctionTriggerReconciler
{
    public static ImmutableList<FunctionHandlerSpec> Reconcile(
        IReadOnlyList<FunctionHandlerSpec> previousHandlers,
        IReadOnlyList<FunctionHandlerSpec> nextHandlers) =>
        nextHandlers.Select(next =>
        {
            var previous = previousHandlers.FirstOrDefault(candidate =>
                candidate.Name == next.Name && candidate.Trigger.GetType() == next.Trigger.GetType());
            if (previous is null)
                return WithIdentity(next, Guid.NewGuid().ToString("N"), scheduleVersion: 1);

            var scheduleVersion = previous.Trigger is TimerFunctionTriggerSpec previousTimer &&
                                  next.Trigger is TimerFunctionTriggerSpec nextTimer
                ? SchedulesEqual(previousTimer, nextTimer)
                    ? previousTimer.ScheduleVersion
                    : checked(previousTimer.ScheduleVersion + 1)
                : 1;
            return WithIdentity(next, previous.Trigger.TriggerId, scheduleVersion);
        }).ToImmutableList();

    private static FunctionHandlerSpec WithIdentity(
        FunctionHandlerSpec handler,
        string triggerId,
        int scheduleVersion) =>
        handler with
        {
            Trigger = handler.Trigger switch
            {
                HttpFunctionTriggerSpec http => http with { TriggerId = triggerId },
                TimerFunctionTriggerSpec timer => timer with
                {
                    TriggerId = triggerId,
                    ScheduleVersion = scheduleVersion
                },
                _ => throw new InvalidOperationException("Unknown Function trigger spec.")
            }
        };

    private static bool SchedulesEqual(
        TimerFunctionTriggerSpec previous,
        TimerFunctionTriggerSpec next) =>
        previous.Cron == next.Cron &&
        previous.TimeZone == next.TimeZone &&
        previous.ConcurrencyPolicy == next.ConcurrencyPolicy &&
        previous.MisfirePolicy == next.MisfirePolicy;
}
