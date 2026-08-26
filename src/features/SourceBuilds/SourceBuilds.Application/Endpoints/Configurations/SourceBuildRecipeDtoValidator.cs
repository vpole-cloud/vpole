using FluentValidation;
using Pole.Features.SourceBuilds.Application.Common;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Configurations;

public sealed class SourceBuildRecipeDtoValidator : AbstractValidator<SourceBuildRecipeDto>
{
    public SourceBuildRecipeDtoValidator()
    {
        RuleFor(item => item.CandidateKey).NotEmpty().MaximumLength(500);
        RuleFor(item => item.SourceRoot).MaximumLength(1000);
        RuleFor(item => item.ManifestPath).NotEmpty().MaximumLength(1000);
        RuleFor(item => item.Commands)
            .NotNull()
            .Must(commands => commands is null || commands.Count <= 50);
        RuleForEach(item => item.Commands).NotEmpty().MaximumLength(2000);
        RuleFor(item => item.StartCommand).MaximumLength(2000);
        RuleFor(item => item.Dockerfile).NotEmpty().MaximumLength(100_000);
    }
}
