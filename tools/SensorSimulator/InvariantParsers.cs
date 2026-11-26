using System.CommandLine.Parsing;
using System.Globalization;

namespace SmartParking.SensorSimulator;

/// <summary>
/// Parsers of the numeric and duration options with the invariant culture. System.CommandLine converts with the
/// current culture, so on a pt-PT machine <c>--mean-interval 0.5</c> was a parse error and only <c>0,5</c> was accepted.
/// The same command line now gives the same run on every machine, which the F12 measurements need (decided by Diogo,
/// 2026-11-26). Used as <c>CustomParser</c>, which System.CommandLine calls only when the option is on the command
/// line; the default values do not go through it.
/// </summary>
internal static class InvariantParsers
{
    private const string Duration = "a duration such as 00:10:00 or 00:00:00.5";

    private delegate bool TryParse<T>(string text, out T value);

    public static int Int32(ArgumentResult result) =>
        Single(result, static (string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value), "an integer");

    public static double Double(ArgumentResult result) =>
        Single(result, static (string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value), "a number with '.' as the decimal separator");

    public static TimeSpan TimeSpan(ArgumentResult result) =>
        Single<TimeSpan>(result, TryParseTimeSpan, Duration);

    public static TimeSpan? OptionalTimeSpan(ArgumentResult result) =>
        result.Tokens.Count == 0 ? null : TimeSpan(result);

    public static TimeSpan[] TimeSpans(ArgumentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var values = new TimeSpan[result.Tokens.Count];
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryParseTimeSpan(result.Tokens[index].Value, out values[index]))
            {
                AddError(result, result.Tokens[index].Value, Duration);
                return [];
            }
        }

        return values;
    }

    private static bool TryParseTimeSpan(string text, out TimeSpan value) =>
        System.TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value);

    private static T Single<T>(ArgumentResult result, TryParse<T> parse, string expected)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = result.Tokens[^1].Value;
        if (parse(text, out var value))
        {
            return value;
        }

        AddError(result, text, expected);
        return default!;
    }

    private static void AddError(ArgumentResult result, string text, string expected)
    {
        var option = result.Parent is OptionResult parent ? parent.Option.Name : "argument";
        result.AddError(string.Create(CultureInfo.InvariantCulture, $"Cannot parse '{text}' for {option}: expected {expected}."));
    }
}
