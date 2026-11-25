using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartParking.Infrastructure.Relational.Migrations;

namespace SmartParking.UnitTests.Infrastructure;

/// <summary>
/// AC-1 (spec 002): the migration only adds the partial unique index; nothing of the Initial migration is removed or
/// altered (no drop, rename or alter operation).
/// </summary>
public sealed class OpenSessionPerVehicleMigrationTests
{
    [Fact]
    public void Up_OnlyCreatesIndex_AC1()
    {
        // Arrange
        var migration = new OpenSessionPerVehicle();

        // Act
        var operations = migration.UpOperations;

        // Assert
        var index = Assert.IsType<CreateIndexOperation>(Assert.Single(operations));
        Assert.Equal("ux_parking_sessions_vehicle_open", index.Name);
        Assert.Equal("parking_sessions", index.Table);
        Assert.Equal(["vehicle_id"], index.Columns);
        Assert.True(index.IsUnique);
        Assert.Equal("status = 'Open'", index.Filter);
    }
}
