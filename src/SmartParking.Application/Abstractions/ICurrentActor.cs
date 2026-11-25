namespace SmartParking.Application.Abstractions;

/// <summary>
/// Who is making the change, as written to <c>audit_entries.actor</c> (RNF-06).
/// Without authentication (spec 001) every change is made by <c>anonymous</c>;
/// the authenticated implementation arrives in F11.
/// </summary>
public interface ICurrentActor
{
    string Name { get; }
}
