using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
namespace Pole.Features.Secrets.Application.Declarations;

internal static class SecretDeclarationRules
{
    public const int MaximumPurposeLength = 500;
    public const string ConsumerBindingRelation = "pole.secrets/consumer-binding";
}
