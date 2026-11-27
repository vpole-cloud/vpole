// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Pole.ASP.Helpers;

public readonly record struct ClaimValue(
    string Value
)
{
    // Minimal APIs will auto-discover this for parameters of type ClaimValue
    public static ValueTask<ClaimValue> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        var claimType = parameter.GetCustomAttribute<FromClaimAttribute>()?.ClaimType;
        var claimValue = context.User?.Claims.FirstOrDefault(x => x.Type == claimType)?.Value;
        if (string.IsNullOrWhiteSpace(claimValue))
            throw new BadHttpRequestException(
                $"Required claim '{claimType}' is missing.",
                StatusCodes.Status401Unauthorized);

        return ValueTask.FromResult(new ClaimValue(claimValue));
    }

    public static implicit operator string(ClaimValue c) => c.Value;
    public static implicit operator Guid(ClaimValue c) => Guid.Parse(c.Value);
}

public readonly record struct ClaimValue<T>(
    T Value
)
{
    public static ValueTask<ClaimValue<T>> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        var claimType = parameter.GetCustomAttribute<FromClaimAttribute>()?.ClaimType;
        var claimValues = string.IsNullOrWhiteSpace(claimType)
            ? []
            : context.User.Claims.Where(x => x.Type == claimType).Select(x => x.Value).ToList();

        var isOptional = IsOptional(parameter);
        if (claimValues.Count == 0 && !isOptional)
            throw new BadHttpRequestException(
                $"Required claim '{claimType}' is missing.",
                StatusCodes.Status401Unauthorized);

        return ValueTask.FromResult(new ClaimValue<T>(ConvertClaimValues<T>(claimValues, isOptional)));
    }

    public static implicit operator T(ClaimValue<T> c) => c.Value;

    private static TValue ConvertClaimValues<TValue>(IReadOnlyCollection<string> values, bool isOptional)
    {
        var targetType = typeof(TValue);
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var effectiveType = nullableType ?? targetType;

        var firstValue = values.FirstOrDefault();
        if (firstValue is null && isOptional)
            return default!;

        if (IsGenericList(effectiveType, out var elementType))
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
            foreach (var value in values)
                list.Add(ConvertSingleValue(value, elementType));

            return (TValue)list;
        }

        return (TValue)ConvertSingleValue(firstValue!, effectiveType);
    }

    private static bool IsOptional(ParameterInfo parameter)
    {
        var nullableType = Nullable.GetUnderlyingType(parameter.ParameterType);
        if (nullableType is not null && IsClaimValueType(nullableType))
            return true;

        if (!parameter.ParameterType.IsGenericType ||
            parameter.ParameterType.GetGenericTypeDefinition() != typeof(ClaimValue<>))
            return false;

        var nullability = new NullabilityInfoContext().Create(parameter);
        return nullability.GenericTypeArguments is [{ ReadState: NullabilityState.Nullable }];
    }

    private static object ConvertSingleValue(string value, Type targetType)
    {
        if (targetType == typeof(string))
            return value;

        if (targetType == typeof(Guid))
            return Guid.Parse(value);

        if (targetType.IsEnum)
            return Enum.Parse(targetType, value, ignoreCase: true);

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static bool IsGenericList(Type type, out Type elementType)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(object);
        return false;
    }

    private static bool IsClaimValueType(Type type) => type == typeof(ClaimValue) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ClaimValue<>));
}
