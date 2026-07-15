using System.Collections.Frozen;
using Pole.Features.Quotas.Contracts;

namespace Pole.Features.Quotas.Application.Common;

public sealed class QuotaCatalog
{
    private readonly Quota[] definitions;
    private readonly FrozenDictionary<QuotaKey, Quota> definitionsByKey;
    private readonly FrozenDictionary<Type, IQuotaProfile> profilesBySpecType;

    public QuotaCatalog(
        IEnumerable<Quota> definitions,
        IEnumerable<IQuotaProfile> profiles)
    {
        var definitionArray = definitions
            .OrderBy(definition => definition.Key.Value, StringComparer.Ordinal)
            .ToArray();
        var duplicateDefinition = definitionArray
            .GroupBy(definition => definition.Key)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDefinition is not null)
        {
            throw new InvalidOperationException(
                $"Quota key '{duplicateDefinition.Key}' is registered more than once.");
        }

        var profileArray = profiles.ToArray();
        var duplicateProfile = profileArray
            .GroupBy(profile => profile.SpecType)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateProfile is not null)
        {
            throw new InvalidOperationException(
                $"More than one quota profile is registered for '{duplicateProfile.Key.FullName}'.");
        }

        this.definitions = definitionArray;
        definitionsByKey = definitionArray.ToFrozenDictionary(definition => definition.Key);
        profilesBySpecType = profileArray.ToFrozenDictionary(profile => profile.SpecType);

        foreach (var profile in profileArray)
        {
            var duplicateKey = profile.Keys
                .GroupBy(key => key)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateKey is not null)
            {
                throw new InvalidOperationException(
                    $"Quota profile for '{profile.SpecType.FullName}' references '{duplicateKey.Key}' more than once.");
            }

            var unknownKeys = profile.Keys
                .Where(key => !definitionsByKey.ContainsKey(key))
                .Select(key => key.Value)
                .ToArray();
            if (unknownKeys.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Quota profile for '{profile.SpecType.FullName}' references unregistered quotas: {string.Join(", ", unknownKeys)}.");
            }
        }
    }

    public IReadOnlyCollection<Quota> Definitions => definitions;

    public bool Contains(QuotaKey key) => definitionsByKey.ContainsKey(key);

    public IQuotaProfile<TSpec> GetProfile<TSpec>()
    {
        if (!profilesBySpecType.TryGetValue(typeof(TSpec), out var profile))
        {
            throw new InvalidOperationException(
                $"No quota profile is registered for '{typeof(TSpec).FullName}'.");
        }

        return (IQuotaProfile<TSpec>)profile;
    }

    public QuotaDelta Calculate<TSpec>(TSpec spec)
    {
        var profile = GetProfile<TSpec>();
        var result = profile.Calculate(spec);
        var undeclaredKeys = result.Values
            .Select(value => value.Key)
            .Where(key => !profile.Keys.Contains(key))
            .Select(key => key.Value)
            .ToArray();
        if (undeclaredKeys.Length > 0)
        {
            throw new InvalidOperationException(
                $"Quota profile for '{typeof(TSpec).FullName}' returned undeclared quotas: {string.Join(", ", undeclaredKeys)}.");
        }

        return result;
    }
}
