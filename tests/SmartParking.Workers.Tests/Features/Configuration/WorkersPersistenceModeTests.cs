using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Workers.Tests.Features.Configuration;

public sealed class WorkersPersistenceModeTests
{
    [Theory]
    [InlineData("hybrid")]
    [InlineData("")]
    [InlineData("Hibrido")]
    [InlineData("0")]
    public void Startup_PersistenceModeEmptyOrInvalid_FailsNamingTheKey_AC9(string mode)
    {
        // Arrange
        var settings = DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid);
        settings[PersistenceOptions.ModeKey] = mode;
        using var factory = new ConfiguredFactory<Program>(settings);

        // Act + Assert
        StartupAssertions.AssertStartupFailsOnPersistenceMode(() => factory.CreateClient().Dispose());
    }
}
