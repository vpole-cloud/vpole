using System.Text.RegularExpressions;

namespace Pole.Workloads;

public readonly partial record struct ResourceTypeId
{
    public const int MaximumLength = 128;

    private ResourceTypeId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ResourceTypeId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Resource type is required.", nameof(value));

        var normalized = value.Trim();
        if (normalized.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"Resource type must be {MaximumLength} characters or less.",
                nameof(value));
        }
        if (!ResourceTypePattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "Resource type must contain lowercase path segments separated by '/'.",
                nameof(value));
        }

        return new ResourceTypeId(normalized);
    }

    public static bool TryCreate(
        string? value,
        out ResourceTypeId resourceType)
    {
        try
        {
            resourceType = Create(value!);
            return true;
        }
        catch (ArgumentException)
        {
            resourceType = default;
            return false;
        }
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?(?:/[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)+$")]
    private static partial Regex ResourceTypePattern();
}

public enum ResourcePlacementCapability
{
    SingleTarget = 0,
    MultiTarget = 1
}

public enum ComputeArchitecture
{
    Any = 0,
    Amd64 = 1,
    Arm64 = 2
}

public enum ComputeNodeAffinityOperator
{
    In = 0,
    NotIn = 1,
    Exists = 2,
    DoesNotExist = 3,
    GreaterThan = 4,
    LessThan = 5
}

public enum ComputeTolerationOperator
{
    Equal = 0,
    Exists = 1
}

public enum ComputeTaintEffect
{
    Any = 0,
    NoSchedule = 1,
    PreferNoSchedule = 2,
    NoExecute = 3
}
