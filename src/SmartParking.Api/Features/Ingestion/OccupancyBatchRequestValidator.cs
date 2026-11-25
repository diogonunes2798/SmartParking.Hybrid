using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using SmartParking.Api.Common;
using SmartParking.Application.Features.Ingestion;
using SmartParking.Domain.Occupancy;

namespace SmartParking.Api.Features.Ingestion;

/// <summary>
/// The batch schema (spec 003, RF-01; AC-2 and AC-3). Any failure rejects the whole batch with a 400 whose keys name
/// the position (<c>events[3].eventId</c>), and nothing is written. The checks:
/// <list type="bullet">
/// <item>all five minimum fields are present;</item>
/// <item>the batch has 1 to 500 events;</item>
/// <item><c>eventId</c> and <c>spotId</c> are UUIDs;</item>
/// <item><c>type</c> is <c>Occupied</c> or <c>Freed</c>;</item>
/// <item><c>timestamp</c> is ISO 8601 with a time zone;</item>
/// <item><c>payload</c>, when present, is an object of at most 4 KB in UTF-8.</item>
/// </list>
/// Registry consistency is not checked here: it is per event and answered with 200 (handler).
/// </summary>
internal sealed partial class OccupancyBatchRequestValidator : AbstractValidator<OccupancyBatchRequest>
{
    public const int MaxPayloadBytes = 4096;

    public OccupancyBatchRequestValidator()
    {
        RuleFor(request => request.Events)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(events => events!.All(item => item is not null)).WithMessage("A batch cannot contain null events.")
            .Must(events => events!.Count is >= 1 and <= IngestOccupancyBatch.MaxEvents)
            .WithMessage($"A batch has between 1 and {IngestOccupancyBatch.MaxEvents} events.")
            .OverridePropertyName("events");

        RuleForEach(request => request.Events)
            .ChildRules(item =>
            {
                item.RuleFor(entry => entry.EventId)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty()
                    .Must(BeUuid).WithMessage("'eventId' must be a UUID.")
                    .OverridePropertyName("eventId");
                item.RuleFor(entry => entry.SensorId).NotEmpty().OverridePropertyName("sensorId");
                item.RuleFor(entry => entry.SpotId)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty()
                    .Must(BeUuid).WithMessage("'spotId' must be a UUID.")
                    .OverridePropertyName("spotId");
                item.RuleFor(entry => entry.Type)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty()
                    .OverridePropertyName("type")
                    .MustBeNameOf<OccupancyEventRequest, OccupancyEventType>("type");
                item.RuleFor(entry => entry.Timestamp)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty()
                    .Must(BeIsoInstantWithOffset)
                    .WithMessage("'timestamp' must be ISO 8601 with a time zone (Z or ±hh:mm).")
                    .OverridePropertyName("timestamp");
                item.RuleFor(entry => entry.Payload)
                    .Must(payload => payload!.Value.ValueKind == JsonValueKind.Object)
                    .WithMessage("'payload' must be a JSON object.")
                    .Must(payload => Encoding.UTF8.GetByteCount(payload!.Value.GetRawText()) <= MaxPayloadBytes)
                    .WithMessage($"'payload' must not exceed {MaxPayloadBytes} bytes.")
                    .OverridePropertyName("payload")
                    .When(entry => entry.Payload is { ValueKind: not JsonValueKind.Null });
            })
            .OverridePropertyName("events")
            .When(request => request.Events is { Count: <= IngestOccupancyBatch.MaxEvents } events && events.All(item => item is not null));
    }

    private static bool BeUuid(string? value) => Guid.TryParse(value, CultureInfo.InvariantCulture, out _);

    private static bool BeIsoInstantWithOffset(string? value) =>
        value is not null
        && IsoInstantWithOffset().IsMatch(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoInstantWithOffset();
}
