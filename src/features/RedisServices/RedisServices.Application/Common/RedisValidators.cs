using FluentValidation;
using Pole.Validation;

namespace Pole.Features.RedisServices.Application.Common;

public sealed class RedisSpecDtoValidator : AbstractValidator<RedisSpecDto>
{
    public RedisSpecDtoValidator()
    {
        RuleFor(x => x.Storage).SetValidator(new StorageDtoValidator());
    }
}

public sealed class StorageDtoValidator : AbstractValidator<StorageDto>
{
    public StorageDtoValidator()
    {
        RuleFor(x => x.SizeGb).GreaterThan(0).LessThanOrEqualTo(1000);
        RuleFor(x => x.StorageProfileId).NotEmpty().MaximumLength(100).MongoObjectId("Storage profile ID");
    }
}
