using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Infrastructure;
using SmartParking.Tests.Common;

namespace SmartParking.Api.Tests.Features.Configuration;

public sealed class PersistenceModeTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Hibrido")]
    [InlineData("hybrid")]
    [InlineData("0")]
    public void Startup_PersistenceModeEmptyOrInvalid_FailsNamingTheKey_AC9(string mode)
    {
        // Arrange
        var settings = DatabaseContainers.UnreachableSettings(PersistenceMode.Hybrid);
        settings[PersistenceOptions.ModeKey] = mode;
        using var factory = ApiFixture.CreateFactory(settings);

        // Act + Assert
        StartupAssertions.AssertStartupFailsOnPersistenceMode(() => factory.CreateClient().Dispose());
    }

    /// <summary>
    /// appsettings.json always ships a mode, so the "missing" case is tested on a host whose
    /// configuration has no Persistence section at all (WebApplicationFactory cannot remove a key).
    /// </summary>
    [Fact]
    public async Task Startup_PersistenceModeMissing_FailsNamingTheKey_AC9()
    {
        // Arrange
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddInfrastructure(new ConfigurationBuilder().Build());
        using var host = builder.Build();

        // Act
        var exception = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));

        // Assert
        var validation = Assert.IsType<OptionsValidationException>(exception);
        Assert.Contains(PersistenceOptions.ModeKey, validation.Message, StringComparison.Ordinal);
    }
}
