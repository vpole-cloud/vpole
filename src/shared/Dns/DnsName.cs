using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

namespace Pole.Dns;

/// <summary>
/// Canonical ASCII DNS name. Unicode input is deliberately not converted in this layer:
/// callers must supply an already encoded punycode label when IDNA is required.
/// </summary>
public sealed record DnsName
{
    public const int MaximumLength = 253;

    private DnsName(string value) => Value = value;

    public string Value { get; }

    public static DnsName Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A DNS name is required.", nameof(value));

        var normalized = value.Trim();
        if (normalized.EndsWith(".", StringComparison.Ordinal))
            normalized = normalized[..^1];
        if (normalized.Length == 0 || normalized.Length > MaximumLength ||
            normalized.Any(character => character > 0x7f) ||
            IPAddress.TryParse(normalized, out _))
        {
            throw new ArgumentException("The value is not a valid ASCII DNS name.", nameof(value));
        }

        var labels = normalized.Split('.', StringSplitOptions.None);
        if (labels.Any(label => !DnsLabel.IsValid(label)))
            throw new ArgumentException("The value is not a valid ASCII DNS name.", nameof(value));

        return new DnsName(normalized.ToLowerInvariant());
    }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out DnsName? name)
    {
        try
        {
            name = value is null ? null : Create(value);
            return name is not null;
        }
        catch (ArgumentException)
        {
            name = null;
            return false;
        }
    }

    public static DnsName Combine(DnsLabel label, DnsName parent)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(parent);
        return Create($"{label.Value}.{parent.Value}");
    }

    public bool IsSameOrDescendantOf(DnsName ancestor)
    {
        ArgumentNullException.ThrowIfNull(ancestor);
        return string.Equals(Value, ancestor.Value, StringComparison.Ordinal) ||
               Value.EndsWith($".{ancestor.Value}", StringComparison.Ordinal);
    }

    public bool IsAncestorOf(DnsName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.IsSameOrDescendantOf(this);
    }

    public override string ToString() => Value;
}

public sealed partial record DnsLabel
{
    public const int MaximumLength = 63;

    private DnsLabel(string value) => Value = value;

    public string Value { get; }

    public static DnsLabel Create(string value)
    {
        if (!IsValid(value))
            throw new ArgumentException("The value is not a valid ASCII DNS label.", nameof(value));

        return new DnsLabel(value.Trim().ToLowerInvariant());
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= MaximumLength &&
        value.Trim().All(character => character <= 0x7f) &&
        LabelPattern().IsMatch(value.Trim());

    public override string ToString() => Value;

    [GeneratedRegex("^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$")]
    private static partial Regex LabelPattern();
}

public sealed record RelativeDnsName
{
    public const string Apex = "@";

    private RelativeDnsName(string value) => Value = value;

    public string Value { get; }
    public bool IsApex => Value == Apex;

    public static RelativeDnsName Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A relative DNS name is required.", nameof(value));

        var normalized = value.Trim();
        if (normalized == Apex)
            return new RelativeDnsName(Apex);
        if (normalized.Length > DnsName.MaximumLength || normalized.EndsWith(".", StringComparison.Ordinal) ||
            normalized.Any(character => character > 0x7f) ||
            normalized.Split('.', StringSplitOptions.None).Any(label => !DnsLabel.IsValid(label)))
        {
            throw new ArgumentException("The value is not a valid relative ASCII DNS name.", nameof(value));
        }

        return new RelativeDnsName(normalized.ToLowerInvariant());
    }

    public DnsName Under(DnsName zone) =>
        IsApex ? zone : DnsName.Create($"{Value}.{zone.Value}");

    public override string ToString() => Value;
}
