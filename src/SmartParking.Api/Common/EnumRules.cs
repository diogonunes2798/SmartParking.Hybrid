using FluentValidation;

namespace SmartParking.Api.Common;

/// <summary>
/// Enum values travel as their exact names (<c>"Active"</c>). <c>Enum.TryParse</c> would also accept numbers and
/// other casings, so the rule compares the names ordinally.
/// </summary>
internal static class EnumRules
{
    public static bool IsName<TEnum>(string? value)
        where TEnum : struct, Enum =>
        value is not null && Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal);

    public static IRuleBuilderOptions<T, string?> MustBeNameOf<T, TEnum>(this IRuleBuilder<T, string?> rule, string field)
        where TEnum : struct, Enum =>
        rule.Must(IsName<TEnum>)
            .WithMessage($"'{field}' must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.")
            .OverridePropertyName(field);

    /// <summary>For optional query filters: absent is fine, present must be a name.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeNameOfWhenPresent<T, TEnum>(this IRuleBuilder<T, string?> rule, string field)
        where TEnum : struct, Enum =>
        rule.Must(value => value is null || IsName<TEnum>(value))
            .WithMessage($"'{field}' must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.")
            .OverridePropertyName(field);

    public static TEnum? ParseOptional<TEnum>(string? value)
        where TEnum : struct, Enum =>
        value is null ? null : Enum.Parse<TEnum>(value);
}
