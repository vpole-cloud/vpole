using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Pole.ASP.Helpers;
using Pole.Mediator;

namespace Pole.Features.Realtime.Application.Endpoints;

public sealed record DeleteRealtimeSessionRequest : IMediatorRequest<DeleteRealtimeSessionOutcome>;
public sealed record RealtimeSessionDeleted;
public union DeleteRealtimeSessionOutcome(RealtimeSessionDeleted);

public sealed class DeleteRealtimeSessionHandler(
    IHttpContextAccessor contexts,
    IRealtimeTicketStore tickets,
    IRealtimeSubscriptions subscriptions,
    IHubContext<RealtimeHub, IRealtimeClient> hub) : IMediatorOutcomeHandler<DeleteRealtimeSessionRequest, DeleteRealtimeSessionOutcome>
{
    public async Task<DeleteRealtimeSessionOutcome> HandleAsync(DeleteRealtimeSessionRequest request, CancellationToken cancellationToken = default)
    {
        var context = contexts.HttpContext!;
        var sessionId = context.User.FindFirst(RealtimeFeature.SessionClaim)!.Value;
        await tickets.RemoveAsync(sessionId);
        var connections = await subscriptions.RevokeSessionAsync(sessionId, cancellationToken);
        if (connections.Count > 0) await hub.Clients.Clients(connections).SessionRevoked();
        await context.SignOutAsync(RealtimeFeature.CookieScheme);
        return new RealtimeSessionDeleted();
    }
}

public static class DeleteRealtimeSessionEndpoint
{
    public static IEndpointRouteBuilder MapDeleteRealtimeSession(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/session", async (IMediator mediator) =>
        {
            var result = await mediator.Send(new DeleteRealtimeSessionRequest());
            return result.ToHttpResult(outcome => outcome switch { RealtimeSessionDeleted => Results.NoContent() });
        }).WithoutTenantContext().RequireAuthorization(policy => policy.AddAuthenticationSchemes(RealtimeFeature.CookieScheme).RequireAuthenticatedUser());
        return endpoints;
    }
}
