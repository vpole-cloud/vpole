using FluentValidation;
using Pole.Features.Shared.Models;

namespace Pole.Features.RedisServices.Application.Common;

public sealed class RedisSpecDtoValidator : AbstractValidator<RedisSpecDto>
{
    public RedisSpecDtoValidator()
    {
        RuleFor(x => x.Storage)
            .SetValidator(new StorageDtoValidator());
        RuleFor(x => x.Resources)
            .SetValidator(new ResourcesValidator());
    }
}

public sealed class StorageDtoValidator : AbstractValidator<StorageDto>
{
    public StorageDtoValidator()
    {
        RuleFor(x => x.SizeGb)
            .GreaterThan(0)
            .LessThanOrEqualTo(1000);
        RuleFor(x => x.StorageProfileId)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public class ResourcesValidator : AbstractValidator<Resources>
{
    public ResourcesValidator()
    {
        RuleFor(x => x.Requests)
            .SetValidator(new ResourceValidator());
        RuleFor(x => x.Limits)
            .SetValidator(new ResourceValidator());
    }
}

public class ResourceValidator : AbstractValidator<Resource>
{
    public ResourceValidator()
    {
        RuleFor(x => x.CpuCores)
            .GreaterThan(0);
        RuleFor(x => x.MemoryMi)
            .GreaterThan(0);
    }
}

