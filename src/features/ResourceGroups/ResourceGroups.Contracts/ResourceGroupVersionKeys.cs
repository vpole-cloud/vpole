namespace Pole.Features.ResourceGroups.Contracts;

public static class ResourceGroupVersionKeys
{
    public static string Revision(string id) => $"ResourceGroup/{id}";

    public static string IsolationScopeRevision(string id) => $"IsolationScope/{id}";

    public static string RegistrationReferenceFence(string id) => $"ResourceRegistration/{id}";
}
