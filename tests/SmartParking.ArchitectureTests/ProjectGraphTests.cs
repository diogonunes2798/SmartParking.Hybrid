using System.Xml.Linq;

namespace SmartParking.ArchitectureTests;

/// <summary>
/// Enforces the dependency rule at project level (ProjectReference / PackageReference / FrameworkReference),
/// complementing <see cref="LayerDependencyTests"/>: NetArchTest only sees types that are actually used in IL,
/// so a forbidden reference that is not (yet) used — or used only through constants — would go unnoticed.
/// The allow-lists below are the executable form of docs/adr/0001.
/// </summary>
public sealed class ProjectGraphTests
{
    private const string Domain = "SmartParking.Domain";
    private const string Application = "SmartParking.Application";
    private const string ServiceDefaults = "SmartParking.ServiceDefaults";

    /// <summary>Project → allowed project references, packages and framework references.</summary>
    private static readonly Dictionary<string, Allowed> Rules = new()
    {
        [Domain] = new(Projects: [], Packages: [], Frameworks: []),
        [Application] = new(Projects: [Domain], Packages: [], Frameworks: []),
        ["SmartParking.Infrastructure.Relational"] = new(
            Projects: [Application],
            // EFCore.NamingConventions: EF Core plug-in (snake_case schema), same engine, no new runtime dependency.
            Packages:
            [
                "Npgsql", "Npgsql.*", "Microsoft.EntityFrameworkCore*", "EFCore.NamingConventions",
                "AspNetCore.HealthChecks.NpgSql", "Microsoft.Extensions.*",
            ],
            Frameworks: []),
        ["SmartParking.Infrastructure.Documents"] = new(
            Projects: [Application],
            Packages: ["MongoDB.*", "AspNetCore.HealthChecks.MongoDb", "Microsoft.Extensions.*"],
            Frameworks: []),
        ["SmartParking.Infrastructure.Cache"] = new(
            Projects: [Application],
            Packages: ["StackExchange.Redis", "Microsoft.Extensions.*"],
            Frameworks: []),
        // Messaging (D2, default Redis Streams) may use the Redis client, but never another sub-layer project.
        ["SmartParking.Infrastructure.Messaging"] = new(
            Projects: [Application],
            Packages: ["StackExchange.Redis", "Microsoft.Extensions.*"],
            Frameworks: []),
        [ServiceDefaults] = new(
            Projects: [Application],
            Packages: ["OpenTelemetry*"],
            Frameworks: ["Microsoft.AspNetCore.App"]),
    };

    public static TheoryData<string> RuledProjects => new(Rules.Keys);

    [Fact]
    public void Every_src_project_is_either_ruled_or_a_host()
    {
        string[] hosts = ["SmartParking.Api", "SmartParking.Workers"];

        var unknown = SrcProjects().Keys.Except(Rules.Keys).Except(hosts).ToArray();

        Assert.True(unknown.Length == 0, "Add an allow-list rule for: " + string.Join(", ", unknown));
    }

    [Theory]
    [MemberData(nameof(RuledProjects))]
    public void Project_references_only_what_its_layer_allows(string project)
    {
        var csproj = XDocument.Load(SrcProjects()[project]);
        var allowed = Rules[project];

        // csproj paths use '\'; normalise so the file name is extracted on any OS.
        var projects = Includes(csproj, "ProjectReference")
            .Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', '/')));
        var packages = Includes(csproj, "PackageReference");
        var frameworks = Includes(csproj, "FrameworkReference");

        string[] violations =
        [
            .. projects.Where(p => !allowed.Projects.Contains(p!)).Select(p => $"ProjectReference {p}"),
            .. packages.Where(p => !allowed.Packages.Any(pattern => Matches(p, pattern))).Select(p => $"PackageReference {p}"),
            .. frameworks.Where(f => !allowed.Frameworks.Contains(f)).Select(f => $"FrameworkReference {f}"),
        ];

        Assert.True(violations.Length == 0, $"{project} violates the dependency rule: " + string.Join(", ", violations));
    }

    private static IEnumerable<string> Includes(XDocument csproj, string item) =>
        csproj.Descendants(item).Select(e => (string?)e.Attribute("Include")).OfType<string>();

    private static bool Matches(string package, string pattern) =>
        pattern.EndsWith('*')
            ? package.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(package, pattern, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> SrcProjects() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => p);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("SmartParking.Hybrid.slnx").Any())
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (SmartParking.Hybrid.slnx) not found.");
    }

    private sealed record Allowed(string[] Projects, string[] Packages, string[] Frameworks);
}
