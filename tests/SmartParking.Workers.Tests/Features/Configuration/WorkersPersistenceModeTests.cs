using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Workers.Tests.Features.Configuration;

public sealed class WorkersPersistenceModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hibrido")]
    public void Startup_PersistenceModeMissingOrInvalid_FailsNamingTheKey_AC9(string? mode)
    {
        // Arrange
        var settings = DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid);
        settings[PersistenceOptions.ModeKey] = mode;
        using var factory = new ConfiguredFactory<Program>(settings);

        // Act + Assert
        StartupAssertions.AssertStartupFailsOnPersistenceMode(() => factory.CreateClient().Dispose());
    }
}
