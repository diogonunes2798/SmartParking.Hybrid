using Microsoft.Extensions.DependencyInjection;
using SmartParking.Application.Abstractions.Payments;

namespace SmartParking.Infrastructure.Payments;

/// <summary>
/// Payment gateway adapter, outside the persistence sublayers. A singleton, so the idempotency state is shared by
/// every request of the process; registered the same way in both persistence modes.
/// </summary>
internal static class PaymentsRegistration
{
    public static IServiceCollection AddPayments(this IServiceCollection services)
    {
        services.AddSingleton<IPaymentGateway, FakePaymentGateway>();
        return services;
    }
}
