namespace SmartParking.Application.Abstractions.Reading;

/// <summary>Read port of the session query (D10). Projection and <c>AsNoTracking</c>.</summary>
public interface ISessionReader
{
    Task<SessionView?> GetAsync(Guid id, CancellationToken cancellationToken);
}
