using FluentValidation;
using System.Text.Json.Serialization;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record DeploymentTargetWorkloadConfigurationRequestBody(
    DeploymentTargetIngressConfigurationRequestBody? Ingress,
    DeploymentTargetNetworkSourcesRequestBody NetworkSources);

public sealed record DeploymentTargetNetworkSourcesRequestBody(
    DeploymentTargetNetworkSourceRequestBody? PublicIngress,
    DeploymentTargetNetworkSourceRequestBody? FunctionGateway,
    DeploymentTargetNetworkSourceRequestBody? PostgresOperator);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DeploymentTargetPodNetworkSourceRequestBody), "Pod")]
[JsonDerivedType(typeof(DeploymentTargetCidrNetworkSourceRequestBody), "Cidr")]
public abstract record DeploymentTargetNetworkSourceRequestBody;

public sealed record DeploymentTargetPodNetworkSourceRequestBody(
    string Namespace,
    Dictionary<string, string> PodLabels) : DeploymentTargetNetworkSourceRequestBody;

public sealed record DeploymentTargetCidrNetworkSourceRequestBody(string Cidr)
    : DeploymentTargetNetworkSourceRequestBody;

public sealed record DeploymentTargetIngressConfigurationRequestBody(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain = null);

public sealed class DeploymentTargetWorkloadConfigurationRequestBodyValidator
    : AbstractValidator<DeploymentTargetWorkloadConfigurationRequestBody>
{
    public DeploymentTargetWorkloadConfigurationRequestBodyValidator()
    {
        RuleFor(body => body.NetworkSources)
            .NotNull()
            .SetValidator(new DeploymentTargetNetworkSourcesRequestBodyValidator());
        When(
            body => body.Ingress is not null,
            () => RuleFor(body => body.Ingress!)
                .SetValidator(new DeploymentTargetIngressConfigurationRequestBodyValidator()));
    }
}

public sealed class DeploymentTargetNetworkSourcesRequestBodyValidator
    : AbstractValidator<DeploymentTargetNetworkSourcesRequestBody>
{
    public DeploymentTargetNetworkSourcesRequestBodyValidator()
    {
        RuleFor(body => body.PublicIngress).SetInheritanceValidator(validator =>
        {
            validator.Add(new DeploymentTargetPodNetworkSourceRequestBodyValidator());
            validator.Add(new DeploymentTargetCidrNetworkSourceRequestBodyValidator());
        });
        RuleFor(body => body.FunctionGateway).SetInheritanceValidator(validator =>
        {
            validator.Add(new DeploymentTargetPodNetworkSourceRequestBodyValidator());
            validator.Add(new DeploymentTargetCidrNetworkSourceRequestBodyValidator());
        });
        RuleFor(body => body.PostgresOperator).SetInheritanceValidator(validator =>
        {
            validator.Add(new DeploymentTargetPodNetworkSourceRequestBodyValidator());
            validator.Add(new DeploymentTargetCidrNetworkSourceRequestBodyValidator());
        });
    }
}

public sealed class DeploymentTargetPodNetworkSourceRequestBodyValidator
    : AbstractValidator<DeploymentTargetPodNetworkSourceRequestBody>
{
    public DeploymentTargetPodNetworkSourceRequestBodyValidator()
    {
        RuleFor(body => body.Namespace)
            .Must(DeploymentTargetPodNetworkSource.IsValidNamespace)
            .WithMessage("Source namespace must be a Kubernetes DNS label.");
        RuleFor(body => body.PodLabels)
            .Must(DeploymentTargetPodNetworkSource.IsValid)
            .WithMessage("Source pod labels must be nonempty valid Kubernetes labels.");
    }
}

public sealed class DeploymentTargetCidrNetworkSourceRequestBodyValidator
    : AbstractValidator<DeploymentTargetCidrNetworkSourceRequestBody>
{
    public DeploymentTargetCidrNetworkSourceRequestBodyValidator()
    {
        RuleFor(body => body.Cidr)
            .Must(DeploymentTargetCidrNetworkSource.IsValid)
            .WithMessage("Source CIDR must be a bounded IP network.");
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
        this DeploymentTargetWorkloadConfigurationRequestBody body) =>
        DeploymentTargetWorkloadConfiguration.Create(
            body.Ingress?.ToDomain(),
            body.NetworkSources.ToDomain());

    public static DeploymentTargetNetworkSources ToDomain(
        this DeploymentTargetNetworkSourcesRequestBody body) =>
        new(
            body.PublicIngress?.ToDomain(),
            body.FunctionGateway?.ToDomain(),
            body.PostgresOperator?.ToDomain());

    public static DeploymentTargetNetworkSource ToDomain(
        this DeploymentTargetNetworkSourceRequestBody body) => body switch
        {
            DeploymentTargetPodNetworkSourceRequestBody pod => DeploymentTargetPodNetworkSource.Create(
                pod.Namespace, pod.PodLabels),
            DeploymentTargetCidrNetworkSourceRequestBody cidr =>
                DeploymentTargetCidrNetworkSource.Create(cidr.Cidr),
            _ => throw new InvalidOperationException("Unsupported network source kind.")
        };

    public static DeploymentTargetIngressConfiguration ToDomain(
        this DeploymentTargetIngressConfigurationRequestBody body) => DeploymentTargetIngressConfiguration.Create(
            body.IngressClassName,
            body.ClusterIssuerName,
            body.PublicBaseDomain);
}
