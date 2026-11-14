namespace SmartParking.Api.IntegrationTests;

/// <summary>
/// The integration tests must run against exactly the same images as the local environment and the
/// experiments (reproducibility rule in CLAUDE.md). Fails if docker-compose.yml and <see cref="ApiFixture"/> drift.
/// </summary>
public sealed class ComposeConsistencyTests
{
    public static TheoryData<string> FixtureImages =>
        new(ApiFixture.PostgresImage, ApiFixture.MongoImage, ApiFixture.RedisImage);

    [Theory]
    [MemberData(nameof(FixtureImages))]
    public void Fixture_image_matches_docker_compose(string image)
    {
        var composeImages = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docker-compose.yml"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("image:", StringComparison.Ordinal))
            .Select(line => line["image:".Length..].Trim());

        Assert.Contains(image, composeImages);
    }

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
}
