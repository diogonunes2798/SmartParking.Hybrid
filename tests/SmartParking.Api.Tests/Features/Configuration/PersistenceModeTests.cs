using SmartParking.Api.Tests.Fixtures;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Api.Tests.Features.Configuration;

public sealed class PersistenceModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hibrido")]
    [InlineData("hybrid")]
    [InlineData("0")]
    public void Startup_PersistenceModeMissingOrInvalid_FailsNamingTheKey_AC9(string? mode)
    {
        // Arrange
        var settings = DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid);
        settings[PersistenceOptions.ModeKey] = mode;
        using var factory = ApiFixture.CreateFactory(settings);

        // Act + Assert
        StartupAssertions.AssertStartupFailsOnPersistenceMode(() => factory.CreateClient().Dispose());
    }
}
