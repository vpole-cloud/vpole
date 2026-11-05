namespace Pole.Architecture;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface |
    AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property |
    AttributeTargets.Field | AttributeTargets.Delegate, AllowMultiple = true, Inherited = false)]
public sealed class ViolateArchitectureAttribute(string rule, string reason) : Attribute
{
    public string Rule { get; } = rule;
    public string Reason { get; } = reason;
}

public static class ArchitectureRules
{
    public const string RawBson = "POLE014";
}
