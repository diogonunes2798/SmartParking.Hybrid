namespace SmartParking.Infrastructure.Documents;

/// <summary>Binds the <c>Mongo</c> section: the database that holds the event log (spec 003).</summary>
public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    public static readonly string ValidationMessage = $"{SectionName}:{nameof(Database)} must not be empty.";

    public string Database { get; set; } = "smartparking";

    public bool IsValid() => !string.IsNullOrWhiteSpace(Database);
}
