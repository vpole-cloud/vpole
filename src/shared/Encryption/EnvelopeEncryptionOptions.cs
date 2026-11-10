using Microsoft.Extensions.Options;

namespace Pole.Encryption;

public sealed class EnvelopeEncryptionOptions
{
    public const string SectionName = "EnvelopeEncryption";

    public string ActiveProviderId { get; set; } = LocalKeyFileKeyEncryptionProvider.StableProviderId;
}

public sealed class EnvelopeEncryptionOptionsValidator(
    IEnumerable<IKeyEncryptionProvider> providers
) : IValidateOptions<EnvelopeEncryptionOptions>
{
    public ValidateOptionsResult Validate(string? name, EnvelopeEncryptionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ActiveProviderId))
            return ValidateOptionsResult.Fail("EnvelopeEncryption:ActiveProviderId is required.");

        var matchingProviders = providers.Count(provider => string.Equals(
            provider.ProviderId,
            options.ActiveProviderId,
            StringComparison.Ordinal));
        return matchingProviders switch
        {
            1 => ValidateOptionsResult.Success,
            0 => ValidateOptionsResult.Fail("EnvelopeEncryption:ActiveProviderId is not registered."),
            _ => ValidateOptionsResult.Fail("EnvelopeEncryption contains duplicate key-encryption provider IDs."),
        };
    }
}
