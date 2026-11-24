namespace SmartParking.Api.Tests.Fixtures;

[CollectionDefinition(Name)]
public sealed class ApiCollectionDefinition : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}
