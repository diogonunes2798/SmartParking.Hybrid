namespace SmartParking.Workers.Tests.Fixtures;

[CollectionDefinition(Name)]
public sealed class WorkersCollectionDefinition : ICollectionFixture<WorkersFixture>
{
    public const string Name = "Workers";
}
