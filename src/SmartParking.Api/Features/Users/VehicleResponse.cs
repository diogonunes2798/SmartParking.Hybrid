using SmartParking.Application.Abstractions.Reading;

namespace SmartParking.Api.Features.Users;

/// <summary>A vehicle on the wire, with the normalised licence plate.</summary>
internal sealed record VehicleResponse(Guid Id, Guid UserId, string LicensePlate)
{
    public static VehicleResponse From(VehicleView view) => new(view.Id, view.UserId, view.LicensePlate);
}
