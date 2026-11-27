using System.Globalization;
using Microsoft.Extensions.Options;
using SmartParking.Api.Common;
using SmartParking.Application.Abstractions;
using SmartParking.Application.Abstractions.Availability;
using SmartParking.Application.Abstractions.Messaging;
using SmartParking.Application.Features.Availability;
using SmartParking.Infrastructure;
using SmartParking.Infrastructure.Hosting;

namespace SmartParking.Api.Features.Availability;

/// <summary>
/// Availability queries (UC-03; RF-03, RF-04; spec 004). They read only the Redis view: no PostgreSQL or MongoDB
/// connection on this path (AC-19). Anonymous on purpose: identity and RBAC belong to RNF-06 (F11).
/// </summary>
internal sealed class AvailabilityEndpoints : IEndpointGroup
{
    private const string NotImplementedTitle = "Availability.NotImplementedInMode";
    private const string NotImplementedDetail = "Availability in Monolithic mode arrives in F9.";

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/availability")
            .WithTags("Availability")
            .AllowAnonymous();

        // The mode filter goes first, so in Monolithic mode even an invalid request is a 501 (nothing is read).
        group.MapGet("/", GetAsync)
            .RequireHybridMode(NotImplementedTitle, NotImplementedDetail)
            .WithRequestValidation<AvailabilityRequest>()
            .Produces<AvailabilityResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/nearby", SearchNearbyAsync)
            .RequireHybridMode(NotImplementedTitle, NotImplementedDetail)
            .WithRequestValidation<NearbyRequest>()
            .Produces<NearbyResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> GetAsync(
        [AsParameters] AvailabilityRequest request,
        IQueryHandler<GetAvailability.Query, IReadOnlyList<FacilityAvailability>> handler,
        IOptions<AvailabilityOptions> options,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToQuery(), cancellationToken);
        return result.Error is { } error
            ? Problem(error, options, response)
            : TypedResults.Ok(new AvailabilityResponse([.. result.Value.Select(FacilityAvailabilityResponse.From)]));
    }

    private static async Task<IResult> SearchNearbyAsync(
        [AsParameters] NearbyRequest request,
        IQueryHandler<SearchNearby.Query, IReadOnlyList<NearbyFacility>> handler,
        IOptions<AvailabilityOptions> options,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToQuery(), cancellationToken);
        return result.Error is { } error
            ? Problem(error, options, response)
            : TypedResults.Ok(new NearbyResponse([.. result.Value.Select(NearbyFacilityResponse.From)]));
    }

    /// <summary>A 503 tells the client when to retry, as the ingestion does (spec 004, ronda 4).</summary>
    private static IResult Problem(Error error, IOptions<AvailabilityOptions> options, HttpResponse response)
    {
        if (error.Type == ErrorType.Unavailable)
        {
            response.Headers.RetryAfter = options.Value.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return error.ToProblem();
    }
}
