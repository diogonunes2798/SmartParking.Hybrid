namespace SmartParking.Api.Common;

internal static class ValidationFilterExtensions
{
    /// <summary>Adds <see cref="ValidationFilter{TRequest}"/> and documents the 400 response.</summary>
    public static RouteHandlerBuilder WithRequestValidation<TRequest>(this RouteHandlerBuilder builder) =>
        builder
            .AddEndpointFilter<ValidationFilter<TRequest>>()
            .ProducesValidationProblem();
}
