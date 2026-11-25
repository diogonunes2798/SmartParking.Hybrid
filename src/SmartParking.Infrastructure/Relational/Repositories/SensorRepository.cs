using Microsoft.EntityFrameworkCore;
using SmartParking.Application.Abstractions.Persistence;
using SmartParking.Domain.Facilities;

namespace SmartParking.Infrastructure.Relational.Repositories;

/// <summary>EF Core implementation of the sensor write port (D10). Tracked on purpose (the unit of work audits the change).</summary>
internal sealed class SensorRepository(SmartParkingDbContext db) : ISensorRepository
{
    private static readonly Func<SmartParkingDbContext, Guid, CancellationToken, Task<Sensor?>> ById =
        EF.CompileAsyncQuery((SmartParkingDbContext context, Guid id, CancellationToken _) =>
            context.Sensors.FirstOrDefault(sensor => sensor.Id == id));

    public Task<Sensor?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => ById(db, id, cancellationToken);

    public void Add(Sensor sensor) => db.Sensors.Add(sensor);

    public void AddRange(IEnumerable<Sensor> sensors) => db.Sensors.AddRange(sensors);

    public void Remove(Sensor sensor) => db.Sensors.Remove(sensor);
}
