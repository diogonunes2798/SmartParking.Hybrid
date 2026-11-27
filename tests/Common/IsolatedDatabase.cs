namespace SmartParking.Tests.Common;

/// <summary>Connection strings of a database created by <see cref="DatabaseContainers.CreateIsolatedDatabaseAsync"/>.</summary>
public sealed record IsolatedDatabase(string OwnerConnectionString, string AppConnectionString, string WorkersConnectionString);
