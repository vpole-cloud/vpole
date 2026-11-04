namespace Pole.Features.Secrets.Application.Persistence;

internal static class SecretDeclarationRules
{
    public const int MaximumPurposeLength = 500;
    public const string ConsumerBindingRelation = "pole.secrets/consumer-binding";
}
