using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

public enum PoleFunctionAgentOperation
{
    Metadata,
    ReserveAttempt,
    ExecuteAttempt,
    ObserveAttempt,
    AgentStatus
}

public sealed record PoleFunctionAgentAuthorizationContext(
    PoleFunctionAgentOperation Operation,
    string? AttemptId = null,
    FunctionAttemptIdentity? Attempt = null,
    bool IsPreflight = false);

public interface IPoleFunctionAgentAuthenticator
{
    ValueTask<bool> AuthorizeAsync(
        HttpContext httpContext,
        PoleFunctionAgentAuthorizationContext authorizationContext,
        CancellationToken cancellationToken);
}

internal sealed class DenyByDefaultAgentAuthenticator(IOptions<PoleFunctionsOptions> options)
    : IPoleFunctionAgentAuthenticator
{
    public ValueTask<bool> AuthorizeAsync(
        HttpContext httpContext,
        PoleFunctionAgentAuthorizationContext authorizationContext,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(options.Value.AllowUnauthenticatedAgentRequests);
}
