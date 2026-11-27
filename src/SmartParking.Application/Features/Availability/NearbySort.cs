namespace SmartParking.Application.Features.Availability;

/// <summary>Order of the radius search (spec 004): <c>distance</c> (default) or <c>free</c>.</summary>
public enum NearbySort
{
    Distance,
    Free,
}
