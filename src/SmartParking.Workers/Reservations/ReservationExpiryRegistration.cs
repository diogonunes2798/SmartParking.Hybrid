namespace SmartParking.Workers.Reservations;

internal static class ReservationExpiryRegistration
{
    /// <summary>The expiry pass and the background service that runs it periodically (spec 002, AC-28).</summary>
    public static IServiceCollection AddReservationExpiry(this IServiceCollection services)
    {
        services.AddSingleton<ReservationExpiryPass>();
        services.AddHostedService<ReservationExpiryWorker>();
        return services;
    }
}
