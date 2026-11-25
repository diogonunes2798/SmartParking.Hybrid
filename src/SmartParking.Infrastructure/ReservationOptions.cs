using SmartParking.Domain.Sessions;

namespace SmartParking.Infrastructure;

/// <summary>
/// Binds the <c>Reservations</c> section (spec 002, «Ciclo da reserva» and «Integrations»). The defaults are the
/// spec's. The Api (fulfilment window, spot protection) and the Workers (expiry) must use the same values (ADR 0007).
/// </summary>
public sealed class ReservationOptions
{
    public const string SectionName = "Reservations";

    public static readonly string ValidationMessage =
        $"{SectionName}:{nameof(MaxDurationHours)}, {SectionName}:{nameof(MaxAdvanceDays)} and " +
        $"{SectionName}:{nameof(ExpiryScanSeconds)} must be greater than 0; {SectionName}:{nameof(EarlyArrivalMinutes)} " +
        $"and {SectionName}:{nameof(ExpiryGraceMinutes)} must be 0 or greater.";

    public int MaxDurationHours { get; set; } = 24;

    public int MaxAdvanceDays { get; set; } = 7;

    public int EarlyArrivalMinutes { get; set; } = 15;

    public int ExpiryGraceMinutes { get; set; } = 15;

    public int ExpiryScanSeconds { get; set; } = 60;

    public bool IsValid() =>
        MaxDurationHours > 0
        && MaxAdvanceDays > 0
        && ExpiryScanSeconds > 0
        && EarlyArrivalMinutes >= 0
        && ExpiryGraceMinutes >= 0;

    public ReservationPolicy ToPolicy() => new(
        TimeSpan.FromHours(MaxDurationHours),
        TimeSpan.FromDays(MaxAdvanceDays),
        TimeSpan.FromMinutes(EarlyArrivalMinutes),
        TimeSpan.FromMinutes(ExpiryGraceMinutes));
}
