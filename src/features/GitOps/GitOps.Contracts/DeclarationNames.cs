using Pole.Workloads;

namespace Pole.Features.GitOps.Contracts;

public static class DeclarationNames
{
    public static bool Valid(string? value) => ResourceTechnicalName.IsValid(value);
}
