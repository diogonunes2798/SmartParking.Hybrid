using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartParking.Api.Tests.Fixtures;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Application.Abstractions.Reading;
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

    /// <summary>
    /// Every write and read port of Application (repositories, readers, unit of work) and the actor resolve in both
    /// modes (spec 001, AC-22). Found by reflection, so the ports added in F2b and later are checked too.
    /// Resolving opens no connection, so no database is needed.
    /// </summary>
    [Theory]
    [InlineData(PersistenceMode.Hybrid)]
    [InlineData(PersistenceMode.Monolithic)]
    public void ConfigurationPorts_EachMode_AllResolve_AC22(PersistenceMode mode)
    {
        // Arrange
        using var factory = ApiFixture.CreateFactory(DatabaseContainers.UnreachableSettings(mode));
        using var scope = factory.Services.CreateScope();
        var ports = typeof(IUnitOfWork).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace is { } ns
                && (ns.EndsWith(".Abstractions.Persistence", StringComparison.Ordinal) || ns.EndsWith(".Abstractions.Reading", StringComparison.Ordinal)))
            .Append(typeof(ICurrentActor))
            .ToList();
        Assert.Contains(typeof(IParkingFacilityRepository), ports);
        Assert.Contains(typeof(IFacilityReader), ports);

        // Act
        var unresolved = ports
            .Where(port => scope.ServiceProvider.GetService(port) is null)
            .Select(port => port.Name)
            .ToList();

        // Assert
        Assert.Empty(unresolved);
    }
}
