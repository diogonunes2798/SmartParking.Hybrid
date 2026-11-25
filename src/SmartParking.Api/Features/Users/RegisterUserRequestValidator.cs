using FluentValidation;
using SmartParking.Domain.Users;

namespace SmartParking.Api.Features.Users;

internal sealed class RegisterUserRequestValidator : AbstractValidator<RegisterUserRequest>
{
    public RegisterUserRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(User.NameMaxLength).OverridePropertyName("name");
        RuleFor(request => request.Email)
            .NotEmpty()
            .MaximumLength(User.EmailMaxLength)
            .EmailAddress()
            .OverridePropertyName("email");
    }
}
