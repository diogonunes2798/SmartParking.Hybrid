using System.CommandLine;

namespace SmartParking.ProjectionRebuilder;

/// <summary>
/// Entry point. Not top-level statements: their global <c>Program</c> type would clash with the Api's <c>Program</c>
/// in the tests, which see both assemblies (<c>InternalsVisibleTo</c>).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args) =>
        await RebuilderCommand.Create().Parse(args).InvokeAsync();
}
