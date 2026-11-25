using SmartParking.Application.Abstractions;

namespace SmartParking.Workers.Hosting;

/// <summary>Actor of every change made by the Workers, written to <c>audit_entries.actor</c> (spec 002, RNF-06).</summary>
internal sealed class SystemCurrentActor : ICurrentActor
{
    public const string SystemName = "system";

    public string Name => SystemName;
}
