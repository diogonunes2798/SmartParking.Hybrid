namespace SmartParking.Tests.Common;

/// <summary>
/// Single source of the Testcontainers images. Must match <c>compose.yaml</c>
/// (checked by the architecture tests, spec 000 AC-14).
/// </summary>
internal static class ContainerImages
{
    public const string Postgres = "postgis/postgis:17-3.7";

    public const string Mongo = "mongo:8.0.38";

    public const string Redis = "redis:8.12.0";

    public static IReadOnlyList<string> All { get; } = [Postgres, Mongo, Redis];
}
