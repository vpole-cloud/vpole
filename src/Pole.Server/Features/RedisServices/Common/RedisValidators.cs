using FluentValidation;
using Pole.Server.Features.WebServices.Common;

namespace Pole.Server.Features.RedisServices.Common;

public sealed class RedisSpecDtoValidator : AbstractValidator<RedisSpecDto>
{
    public RedisSpecDtoValidator()
    {
        RuleFor(x => x.Storage)
            .SetValidator(new StorageDtoValidator());
        RuleFor(x => x.Resources)
            .SetValidator(new ResourcesDtoValidator());
    }
}

public sealed class StorageDtoValidator : AbstractValidator<StorageDto>
{
    public StorageDtoValidator()
    {
        RuleFor(x => x.SizeGb)
            .GreaterThan(0)
            .LessThanOrEqualTo(1000);
        RuleFor(x => x.StorageClassName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public class ResourcesDtoValidator : AbstractValidator<ResourcesDto>
{
    public ResourcesDtoValidator()
    {
        RuleFor(x => x.Requests)
            .SetValidator(new ResourceDtoValidator());
        RuleFor(x => x.Limits)
            .SetValidator(new ResourceDtoValidator());
    }
}

public class ResourceDtoValidator : AbstractValidator<ResourceDto>
{
    public ResourceDtoValidator()
    {
        RuleFor(x => x.CpuCores)
            .GreaterThan(0);
        RuleFor(x => x.MemoryMi)
            .GreaterThan(0);
    }
}
