namespace SmartParking.Infrastructure;

/// <summary>Which implementations back the persistence ports (Cap. 7.5 of the thesis).</summary>
public enum PersistenceMode
{
    /// <summary>PostgreSQL + MongoDB + Redis.</summary>
    Hybrid,

    /// <summary>Everything in PostgreSQL (comparison baseline, F9).</summary>
    Monolithic,
}
