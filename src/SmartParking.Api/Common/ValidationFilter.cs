using FluentValidation;

namespace SmartParking.Api.Common;

/// <summary>
/// Endpoint filter that runs the FluentValidation validator of <typeparamref name="TRequest"/>
/// before the handler. An invalid request is answered with 400 ValidationProblemDetails
/// and the handler is not called.
/// </summary>
internal sealed class ValidationFilter<TRequest>(IValidator<TRequest> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return TypedResults.Problem(
                detail: "The request body is missing.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var validation = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        return validation.IsValid
            ? await next(context)
            : TypedResults.ValidationProblem(validation.ToDictionary());
    }
}
