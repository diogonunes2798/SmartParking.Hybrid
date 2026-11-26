namespace SmartParking.Application.Abstractions.Documents;

/// <summary>The <c>eventId</c>s that were already in the log; every other event of the call was written.</summary>
public sealed record AppendOutcome(IReadOnlySet<Guid> Duplicates);
