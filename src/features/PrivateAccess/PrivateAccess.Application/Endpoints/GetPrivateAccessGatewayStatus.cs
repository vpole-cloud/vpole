using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Application.Persistence;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

[JsonConverter(typeof(JsonStringEnumConverter<PrivateAccessGatewayAvailability>))]
public enum PrivateAccessGatewayAvailability
{
    NotRequired,
    FeatureDisabled,
    TargetDisabled,
    TargetDeletionRequested,
    NetworkPolicyUnavailable,
    Provisioning,
    AwaitingConnection,
    Disconnected,
    CredentialExpired,
    Removing,
    Connected
}

public sealed record PrivateAccessGatewayStatusDto(
    string TenantId,
    string DeploymentTargetId,
    bool Required,
    PrivateAccessGatewayAvailability Availability,
    DateTime ObservedAtUtc,
    DateTime? LastContactAtUtc,
    string? ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    long? CredentialGeneration,
    DateTime? CredentialExpiresAtUtc);

public sealed record GetPrivateAccessGatewayStatusRequest(
    [FromRoute] string TenantId,
    [FromRoute] string DeploymentTargetId)
    : IMediatorRequest<GetPrivateAccessGatewayStatusOutcome>, IPlatformTenantOperation;

public union GetPrivateAccessGatewayStatusOutcome(
    PrivateAccessGatewayStatusDto,
    DeploymentTargetNotFound);

public sealed class GetPrivateAccessGatewayStatusHandler(
    LookupDeploymentTarget lookupTarget,
    LoadPrivateAccessGatewayDesiredStates loadDesired,
    LoadPrivateAccessGatewayObservation loadObservation,
    IConfiguration configuration,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        GetPrivateAccessGatewayStatusRequest,
        GetPrivateAccessGatewayStatusOutcome>
{
    public async Task<GetPrivateAccessGatewayStatusOutcome> HandleAsync(
        GetPrivateAccessGatewayStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await lookupTarget(request.DeploymentTargetId, cancellationToken);
        if (target is not DeploymentTargetLookupDto found)
            return new DeploymentTargetNotFound(request.DeploymentTargetId);

        var desired = await loadDesired(cancellationToken);
        var required = desired.Any(state =>
            state.TenantId == request.TenantId &&
            state.DeploymentTargetId == request.DeploymentTargetId &&
            state.Endpoints.Count > 0);
        var observation = await loadObservation(
            request.TenantId, request.DeploymentTargetId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var enabled = configuration.GetValue<bool>("PrivateAccess:Enabled");
        var availability = Availability(found, enabled, required, observation, now);
        var capabilities = observation?.ProtocolVersion == PrivateAccessGatewayProtocolMetadata.Version
            ? new[] { PrivateAccessGatewayProtocolMetadata.TcpForwardCapability }
            : [];
        return new PrivateAccessGatewayStatusDto(
            request.TenantId,
            request.DeploymentTargetId,
            required,
            availability,
            now,
            observation?.LastContactAtUtc,
            observation?.ProtocolVersion,
            capabilities,
            observation?.CredentialGeneration,
            observation?.CredentialExpiresAtUtc);
    }

    private static PrivateAccessGatewayAvailability Availability(
        DeploymentTargetLookupDto target,
        bool enabled,
        bool required,
        PrivateAccessGatewayObservation? observation,
        DateTime nowUtc)
    {
        if (!enabled)
            return PrivateAccessGatewayAvailability.FeatureDisabled;
        if (!target.Enabled)
            return PrivateAccessGatewayAvailability.TargetDisabled;
        if (target.DeletionRequested)
            return PrivateAccessGatewayAvailability.TargetDeletionRequested;
        if (target.NetworkPolicy.Readiness != NetworkPolicyReadiness.EnforcementAttested)
            return PrivateAccessGatewayAvailability.NetworkPolicyUnavailable;
        if (!required)
            return observation is null
                ? PrivateAccessGatewayAvailability.NotRequired
                : PrivateAccessGatewayAvailability.Removing;
        if (observation is null)
            return PrivateAccessGatewayAvailability.Provisioning;
        if (observation.CredentialExpiresAtUtc <= nowUtc)
            return PrivateAccessGatewayAvailability.CredentialExpired;
        if (observation.BrokerLeaseActive && observation.LastContactAtUtc >= nowUtc.AddSeconds(-20))
            return PrivateAccessGatewayAvailability.Connected;
        return observation.LastContactAtUtc is null
            ? PrivateAccessGatewayAvailability.AwaitingConnection
            : PrivateAccessGatewayAvailability.Disconnected;
    }
}

public sealed class GetPrivateAccessGatewayStatusValidator
    : AbstractValidator<GetPrivateAccessGatewayStatusRequest>
{
    public GetPrivateAccessGatewayStatusValidator()
    {
        RuleFor(request => request.TenantId).MongoObjectId("Tenant ID");
        RuleFor(request => request.DeploymentTargetId).MongoObjectId("Deployment target ID");
    }
}

public static class GetPrivateAccessGatewayStatusEndpoint
{
    public static IEndpointRouteBuilder MapGetPrivateAccessGatewayStatusEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/{TenantId}/{DeploymentTargetId}", async (
                IMediator mediator,
                [AsParameters] GetPrivateAccessGatewayStatusRequest request,
                CancellationToken cancellationToken) =>
            (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
            {
                PrivateAccessGatewayStatusDto status => Results.Ok(status),
                DeploymentTargetNotFound missing => Results.NotFound(missing)
            }))
            .WithName("GetPrivateAccessGatewayStatus")
            .Produces<PrivateAccessGatewayStatusDto>()
            .Produces<DeploymentTargetNotFound>(StatusCodes.Status404NotFound);
        return app;
    }
}
