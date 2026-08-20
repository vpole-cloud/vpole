namespace Pole.ResultType;

/// <summary>
/// Represents a successful typed operation that has no data to return.
/// </summary>
public readonly record struct Unit
{
    public static Unit Value => default;
}
