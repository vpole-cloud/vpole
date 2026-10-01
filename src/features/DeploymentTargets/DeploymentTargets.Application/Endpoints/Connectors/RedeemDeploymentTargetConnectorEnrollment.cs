using System.Security.Cryptography;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Persistence.Connectors;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints.Connectors;

public sealed record RedeemDeploymentTargetConnectorEnrollmentBody(
    string Token,
    string CertificateDerBase64)
{
    public override string ToString() =>
        "RedeemDeploymentTargetConnectorEnrollmentBody(Token=[REDACTED], Certificate=[REDACTED])";
}

public sealed record RedeemDeploymentTargetConnectorEnrollmentRequest(
    [FromRoute] string Id,
    [FromBody] RedeemDeploymentTargetConnectorEnrollmentBody Body)
    : IMediatorRequest<RedeemDeploymentTargetConnectorEnrollmentRequestOutcome>;

public union RedeemDeploymentTargetConnectorEnrollmentRequestOutcome(
    DeploymentTargetConnectorEnrolled,
    DeploymentTargetConnectorEnrollmentRejected,
    DeploymentTargetConnectorCertificateConflict,
    DeploymentTargetConnectorCertificateRejected);

public sealed class RedeemDeploymentTargetConnectorEnrollmentHandler(
    RedeemDeploymentTargetConnectorEnrollment redeemEnrollment,
    TimeProvider clock)
    : IMediatorOutcomeHandler<
        RedeemDeploymentTargetConnectorEnrollmentRequest,
        RedeemDeploymentTargetConnectorEnrollmentRequestOutcome>
{
    public async Task<RedeemDeploymentTargetConnectorEnrollmentRequestOutcome> HandleAsync(
        RedeemDeploymentTargetConnectorEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var certificate = ConnectorCertificateValidation.Validate(
            request.Body.CertificateDerBase64,
            clock.GetUtcNow().UtcDateTime);
        if (certificate is null)
            return new DeploymentTargetConnectorCertificateRejected();

        var tokenBytes = Convert.FromHexString(request.Body.Token);
        try
        {
            var tokenDigest = Convert.ToHexString(SHA256.HashData(tokenBytes));
            var result = await redeemEnrollment(
                request.Id,
                tokenDigest,
                certificate.FingerprintSha256,
                certificate.PublicKeySpkiSha256,
                certificate.NotAfterUtc,
                cancellationToken);
            return result switch
            {
                DeploymentTargetConnectorEnrolled enrolled => enrolled,
                DeploymentTargetConnectorEnrollmentRejected rejected => rejected,
                DeploymentTargetConnectorCertificateConflict conflict => conflict,
                DeploymentTargetConnectorCertificateRejected rejected => rejected
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }
}

public sealed class RedeemDeploymentTargetConnectorEnrollmentRequestValidator
    : AbstractValidator<RedeemDeploymentTargetConnectorEnrollmentRequest>
{
    public RedeemDeploymentTargetConnectorEnrollmentRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
        RuleFor(request => request.Body).NotNull();
        RuleFor(request => request.Body.Token)
            .Matches("^[0-9A-Fa-f]{64}$")
            .When(request => request.Body is not null);
        RuleFor(request => request.Body.CertificateDerBase64)
            .NotEmpty()
            .MaximumLength(24_000)
            .When(request => request.Body is not null);
    }
}

public static class RedeemDeploymentTargetConnectorEnrollmentEndpoint
{
    public static IEndpointRouteBuilder MapRedeemDeploymentTargetConnectorEnrollmentEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/connectors/enroll", async (
                IMediator mediator,
                [AsParameters] RedeemDeploymentTargetConnectorEnrollmentRequest request,
                CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                DeploymentTargetConnectorEnrolled enrolled => Results.Ok(enrolled),
                DeploymentTargetConnectorEnrollmentRejected error => DeploymentTargetHttpMappings.Map(error),
                DeploymentTargetConnectorCertificateConflict error => DeploymentTargetHttpMappings.Map(error),
                DeploymentTargetConnectorCertificateRejected error => DeploymentTargetHttpMappings.Map(error)
            });
        })
            .AllowAnonymous()
            .WithName("RedeemDeploymentTargetConnectorEnrollment")
            .Produces<DeploymentTargetConnectorEnrolled>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }
}
