using System.Runtime.CompilerServices;

namespace SmartParking.Domain.Common;

/// <summary>
/// Construction invariants. Input is validated at the API boundary (FluentValidation); a failure here
/// is a programming error, so it throws instead of returning a Result.
/// </summary>
internal static class Guard
{
    public static string Text(string? value, int maxLength, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);

        var trimmed = value.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, maxLength, paramName);
        return trimmed;
    }

    public static Guid Id(Guid value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", paramName);
        }

        return value;
    }

    public static TEnum Defined<TEnum>(TEnum value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Unknown value.");
        }

        return value;
    }
}
