using Pole.Features.Request;
using Pole.Mediator;
using Xunit.Sdk;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public static class ResultAssertionExtensions
{
    public static T AssertSuccess<T>(this MediatorResult<T> result) where T : notnull =>
        result.Match(outcome => outcome, rejected => throw new XunitException($"Expected a handler outcome, but received '{rejected.Reason.GetType().Name}'."));

    public static RequestValidationRejected AssertValidationRejected<T>(this MediatorResult<T> result) where T : notnull =>
        result.Match(_ => throw new XunitException("Expected request validation rejection."), rejected => rejected.Reason as RequestValidationRejected ?? throw new XunitException($"Expected validation rejection, but received '{rejected.Reason.GetType().Name}'."));
}
