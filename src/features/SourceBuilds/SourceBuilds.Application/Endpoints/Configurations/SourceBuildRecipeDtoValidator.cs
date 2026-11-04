using FluentValidation;
using Pole.Features.SourceBuilds.Application.Common;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Configurations;

public sealed class SourceBuildRecipeDtoValidator : AbstractValidator<SourceBuildRecipeDto>
{
    public SourceBuildRecipeDtoValidator()
    {
        RuleFor(item => item.Dockerfile).NotEmpty().MaximumLength(100_000);
    }
}
