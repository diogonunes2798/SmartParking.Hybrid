namespace SmartParking.Domain.Common;

/// <summary>
/// Entity with an optimistic-concurrency version. The value is assigned by the store
/// (PostgreSQL <c>xmin</c> in the relational sub-layer) and never set by domain code.
/// </summary>
public interface IVersioned
{
    uint Version { get; }
}
