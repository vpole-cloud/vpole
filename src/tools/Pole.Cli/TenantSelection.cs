using Pole.Cli.Api;

namespace Pole.Cli;

internal abstract record TenantSelectionResult;

internal sealed record TenantSelectionFound(TenantDto Tenant) : TenantSelectionResult;

internal sealed record TenantSelectionAmbiguous(IReadOnlyList<TenantDto> Matches) : TenantSelectionResult;

internal sealed record TenantSelectionNotFound : TenantSelectionResult;

internal static class TenantSelection
{
    public static TenantSelectionResult Resolve(string input, IReadOnlyList<TenantDto> tenants)
    {
        var idMatches = tenants.Where(tenant => string.Equals(tenant.Id, input, StringComparison.Ordinal)).ToArray();
        if (idMatches.Length == 1)
            return new TenantSelectionFound(idMatches[0]);
        if (idMatches.Length > 1)
            return new TenantSelectionAmbiguous(idMatches);

        var nameMatches = tenants.Where(tenant => string.Equals(tenant.Name, input, StringComparison.Ordinal)).ToArray();
        return nameMatches.Length switch
        {
            0 => new TenantSelectionNotFound(),
            1 => new TenantSelectionFound(nameMatches[0]),
            _ => new TenantSelectionAmbiguous(nameMatches)
        };
    }
}
