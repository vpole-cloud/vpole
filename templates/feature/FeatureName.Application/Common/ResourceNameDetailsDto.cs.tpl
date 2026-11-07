using FluentValidation;
using Pole.Features.FeatureName.Domain;

namespace Pole.Features.FeatureName.Application.Common;

public sealed record ResourceNameDetailsDto(
    string Name,
    string Description);

public sealed class ResourceNameDetailsValidator : AbstractValidator<ResourceNameDetailsDto>
{
    public ResourceNameDetailsValidator()
    {
        RuleFor(details => details.Name).NotEmpty().MaximumLength(ResourceNameDetails.MaximumNameLength);
        RuleFor(details => details.Description)
            .NotNull().MaximumLength(ResourceNameDetails.MaximumDescriptionLength);
    }
}
