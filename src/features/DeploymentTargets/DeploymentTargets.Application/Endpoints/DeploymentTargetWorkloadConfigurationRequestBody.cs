using FluentValidation;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record DeploymentTargetWorkloadConfigurationRequestBody(
    DeploymentTargetIngressConfigurationRequestBody? Ingress);

public sealed record DeploymentTargetIngressConfigurationRequestBody(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain = null);

public sealed class DeploymentTargetWorkloadConfigurationRequestBodyValidator
    : AbstractValidator<DeploymentTargetWorkloadConfigurationRequestBody>
{
    public DeploymentTargetWorkloadConfigurationRequestBodyValidator()
    {
        When(
            body => body.Ingress is not null,
            () => RuleFor(body => body.Ingress!)
                .SetValidator(new DeploymentTargetIngressConfigurationRequestBodyValidator()));
    }
}

public sealed class DeploymentTargetIngressConfigurationRequestBodyValidator
    : AbstractValidator<DeploymentTargetIngressConfigurationRequestBody>
{
    public DeploymentTargetIngressConfigurationRequestBodyValidator()
    {
        RuleFor(body => body.IngressClassName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(DeploymentTargetIngressConfiguration.MaximumNameLength)
            .Must(DeploymentTargetIngressConfiguration.IsValidKubernetesResourceName)
            .WithMessage("Ingress class name must be a valid Kubernetes resource name.");
        RuleFor(body => body.ClusterIssuerName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(DeploymentTargetIngressConfiguration.MaximumNameLength)
            .Must(DeploymentTargetIngressConfiguration.IsValidKubernetesResourceName)
            .WithMessage("Cluster issuer name must be a valid Kubernetes resource name.");
        RuleFor(body => body.PublicBaseDomain)
            .Must(value => value is null || DeploymentTargetIngressConfiguration.IsValidPublicBaseDomain(value))
            .WithMessage("Public base domain must be a valid ASCII DNS name.");
    }
}

internal static class DeploymentTargetWorkloadConfigurationRequestBodyExtensions
{
    public static DeploymentTargetWorkloadConfiguration ToDomain(
        this DeploymentTargetWorkloadConfigurationRequestBody body) => DeploymentTargetWorkloadConfiguration.Create(body.Ingress?.ToDomain());

    public static DeploymentTargetIngressConfiguration ToDomain(
        this DeploymentTargetIngressConfigurationRequestBody body) => DeploymentTargetIngressConfiguration.Create(
            body.IngressClassName,
            body.ClusterIssuerName,
            body.PublicBaseDomain);
}
