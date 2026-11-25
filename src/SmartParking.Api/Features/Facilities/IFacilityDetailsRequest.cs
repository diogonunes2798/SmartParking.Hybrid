namespace SmartParking.Api.Features.Facilities;

/// <summary>Fields shared by the create (POST) and replace (PUT) requests, validated by <see cref="FacilityDetailsValidator"/>.</summary>
internal interface IFacilityDetailsRequest
{
    string? Name { get; }

    string? Address { get; }

    double Latitude { get; }

    double Longitude { get; }

    string? TimeZone { get; }

    IReadOnlyList<OpeningHoursRequest>? OpeningHours { get; }
}
