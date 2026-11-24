using FluentValidation;

namespace SmartParking.Api.Tests.Fixtures;

internal sealed class TestRequestValidator : AbstractValidator<TestRequest>
{
    public TestRequestValidator() => RuleFor(request => request.Name).NotEmpty();
}
