namespace SmartParking.Application.Abstractions.Reading;

/// <summary>
/// Read port of the ingestion's registry check (spec 003, «Validação contra o registo»): one query per batch for all
/// its hardware ids, no cache. Sensors that do not exist are simply absent from the result. An unreachable database
/// is an <see cref="ErrorType.Unavailable"/> error.
/// </summary>
public interface ISensorRegistryReader
{
    Task<Result<IReadOnlyList<SensorRegistration>>> GetByHardwareIdsAsync(
        IReadOnlyCollection<string> hardwareIds,
        CancellationToken cancellationToken);
}
